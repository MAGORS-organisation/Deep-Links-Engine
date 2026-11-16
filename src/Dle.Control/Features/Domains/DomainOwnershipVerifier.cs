using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Domains;

/// <summary>
/// Resolves DNS TXT records for a host name, abstracting network DNS so tests stay deterministic.
/// </summary>
public interface IDnsTxtResolver
{
    /// <summary>Resolves all TXT record strings published at <paramref name="hostName"/>.</summary>
    /// <param name="hostName">Fully qualified record name, e.g. <c>_dle-verify.go.example.com</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All decoded TXT strings, or empty when none exist / lookup fails.</returns>
    Task<IReadOnlyList<string>> ResolveTxtAsync(string hostName, CancellationToken cancellationToken);
}

/// <summary>
/// Zero-dependency RFC 1035 DNS TXT resolver over UDP port 53 using configured OS resolvers.
/// </summary>
public sealed class UdpDnsTxtResolver : IDnsTxtResolver
{
    private const int DnsPort = 53;
    private const ushort TypeTxt = 16;
    private const ushort ClassIn = 1;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveTxtAsync(string hostName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        byte[] query = BuildQuery(hostName);
        foreach (IPAddress server in DiscoverNameServers())
        {
            try
            {
                using var client = new UdpClient(server.AddressFamily);
                client.Connect(server, DnsPort);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(QueryTimeout);

                await client.SendAsync(query, cts.Token);
                UdpReceiveResult response = await client.ReceiveAsync(cts.Token);

                if (TryParseTxtAnswers(response.Buffer, query.AsSpan(0, 2), out IReadOnlyList<string> answers))
                {
                    return answers;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next configured nameserver on transient timeout or socket error.
            }
        }

        return [];
    }

    internal static byte[] BuildQuery(string hostName)
    {
        string trimmed = hostName.Trim().TrimEnd('.');
        Span<byte> txId = stackalloc byte[2];
        RandomNumberGenerator.Fill(txId);

        using var ms = new MemoryStream(64);
        // Header (12 bytes): ID, Flags (RD=1 -> 0x0100), QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0.
        ms.Write(txId);
        ms.Write([0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        foreach (string label in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException("DNS label exceeds 63 bytes.", nameof(hostName));
            }

            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.WriteByte(0); // Root terminator.
        // QTYPE=TXT (16), QCLASS=IN (1)
        ms.Write([0x00, 0x10, 0x00, 0x01]);
        return ms.ToArray();
    }

    internal static bool TryParseTxtAnswers(
        byte[] packet,
        ReadOnlySpan<byte> expectedTxId,
        out IReadOnlyList<string> results)
    {
        results = [];
        if (packet.Length < 12 || !packet.AsSpan(0, 2).SequenceEqual(expectedTxId))
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
        {
            // Not a response or RCODE != NOERROR (e.g. NXDOMAIN / SERVFAIL).
            return false;
        }

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        int offset = 12;

        for (int q = 0; q < qdCount; q++)
        {
            if (!SkipName(packet, ref offset) || offset + 4 > packet.Length)
            {
                return false;
            }

            offset += 4; // QTYPE + QCLASS
        }

        List<string> txts = [];
        for (int a = 0; a < anCount; a++)
        {
            if (!SkipName(packet, ref offset) || offset + 10 > packet.Length)
            {
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2, 2));
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > packet.Length)
            {
                break;
            }

            if (type == TypeTxt && rrClass == ClassIn)
            {
                int rrEnd = offset + rdLength;
                int cursor = offset;
                var sb = new StringBuilder(rdLength);
                while (cursor < rrEnd)
                {
                    int strLen = packet[cursor++];
                    if (cursor + strLen > rrEnd)
                    {
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(packet, cursor, strLen));
                    cursor += strLen;
                }

                if (sb.Length > 0)
                {
                    txts.Add(sb.ToString());
                }
            }

            offset += rdLength;
        }

        results = txts;
        return true;
    }

    private static bool SkipName(byte[] packet, ref int offset)
    {
        int hops = 0;
        while (offset < packet.Length)
        {
            byte len = packet[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 2 > packet.Length || ++hops > 16)
                {
                    return false;
                }

                offset += 2;
                return true;
            }

            offset += 1 + len;
        }

        return false;
    }

    private static List<IPAddress> DiscoverNameServers()
    {
        List<IPAddress> servers = [];

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (IPAddress dns in nic.GetIPProperties().DnsAddresses)
                {
                    if (!dns.IsIPv6SiteLocal && !servers.Contains(dns))
                    {
                        servers.Add(dns);
                    }
                }
            }
        }
        catch
        {
            // Fall back to /etc/resolv.conf or public recursive resolvers below.
        }

        if (servers.Count == 0 && File.Exists("/etc/resolv.conf"))
        {
            try
            {
                foreach (string line in File.ReadLines("/etc/resolv.conf"))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("nameserver ", StringComparison.OrdinalIgnoreCase))
                    {
                        string addr = trimmed["nameserver ".Length..].Trim();
                        if (IPAddress.TryParse(addr, out IPAddress? parsed) && !servers.Contains(parsed))
                        {
                            servers.Add(parsed);
                        }
                    }
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        if (servers.Count == 0)
        {
            servers.Add(IPAddress.Parse("1.1.1.1"));
            servers.Add(IPAddress.Parse("9.9.9.9"));
        }

        return servers;
    }
}

/// <summary>
/// Derives per-tenant cryptographic DNS TXT challenges (<c>_dle-verify.&lt;host&gt;</c>) and
/// verifies domain ownership before activation in multi-tenant environments.
/// </summary>
public sealed class DomainOwnershipVerifier
{
    private const string ChallengePrefix = "dle-verify=";
    private const string TxtSubdomainPrefix = "_dle-verify.";

    private readonly byte[] _hmacKey;
    private readonly IDnsTxtResolver _dns;

    /// <summary>Creates the ownership verifier.</summary>
    /// <param name="cryptoOptions">Crypto options providing the master secret.</param>
    /// <param name="dns">DNS TXT resolver.</param>
    public DomainOwnershipVerifier(IOptions<CryptoOptions> cryptoOptions, IDnsTxtResolver dns)
    {
        ArgumentNullException.ThrowIfNull(cryptoOptions);
        ArgumentNullException.ThrowIfNull(dns);

        byte[] ikm = Encoding.UTF8.GetBytes(cryptoOptions.Value.MasterSecret);
        _hmacKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            outputLength: 32,
            salt: null,
            info: "dle:domain-ownership:v1"u8.ToArray());

        _dns = dns;
    }

    /// <summary>Returns the TXT record host name to publish for <paramref name="host"/>.</summary>
    /// <param name="host">Normalized domain host.</param>
    /// <returns><c>_dle-verify.&lt;host&gt;</c>.</returns>
    public static string GetTxtHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return TxtSubdomainPrefix + host;
    }

    /// <summary>Computes the deterministic TXT challenge bound to <paramref name="tenantId"/> and <paramref name="host"/>.</summary>
    /// <param name="tenantId">Owning tenant identifier.</param>
    /// <param name="host">Normalized domain host.</param>
    /// <returns>TXT value prefixed with <c>dle-verify=</c>.</returns>
    public string GetExpectedTxtValue(Guid tenantId, string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string payload = $"{tenantId:N}:{host}";
        byte[] mac = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(payload));
        string encoded = Convert.ToBase64String(mac, 0, 24)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return ChallengePrefix + encoded;
    }

    /// <summary>Queries DNS TXT at <c>_dle-verify.&lt;host&gt;</c> and confirms the tenant challenge.</summary>
    /// <param name="domain">The domain being verified.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a matching TXT record is present.</returns>
    public async Task<bool> VerifyAsync(LinkDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        string expected = GetExpectedTxtValue(domain.TenantId, domain.Host);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        IReadOnlyList<string> records = await _dns.ResolveTxtAsync(GetTxtHost(domain.Host), cancellationToken);
        foreach (string record in records)
        {
            byte[] candidateBytes = Encoding.UTF8.GetBytes(record.Trim());
            if (candidateBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes))
            {
                return true;
            }
        }

        return false;
    }
}
