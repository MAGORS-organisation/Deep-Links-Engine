using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Dle.Domain.Primitives;

namespace Dle.Domain.Abuse;

/// <summary>
/// Syntactic and network level validation of link targets (threats T-01 and T-02, TC-161 to
/// TC-164). This is the gate that decides whether the product survives its first year or ends up
/// on blocklists (§E.3).
/// </summary>
/// <remarks>
/// <para>
/// The checks here are pure and offline. <see cref="ValidateSyntax(string?)"/> is step one of
/// §E.3: scheme, length, credentials and host shape, plus the address checks that can be made
/// without a network round trip when the host is an IP literal. Step two, resolving the host and
/// checking every resulting address, is what actually stops SSRF and DNS rebinding; it needs a
/// resolver and therefore lives behind <c>IUrlSafetyChecker</c>, which calls
/// <see cref="IsForbiddenAddress(IPAddress)"/> for every address it gets back, and has to repeat
/// the check at connection time rather than trusting the first answer.
/// </para>
/// <para>
/// Everything here fails closed. An address family we do not recognise, a host we cannot
/// normalize and a literal we cannot parse are all refused.
/// </para>
/// </remarks>
public static class TargetUrlPolicy
{
    /// <summary>The only schemes a link target may use.</summary>
    public static readonly string[] AllowedSchemes = ["http", "https"];

    /// <summary>Maximum length of a target URL in characters.</summary>
    public const int MaxUrlLength = 2048;

    private const string SyntaxSource = "syntax";
    private const string AddressSource = "private_ip";

    /// <summary>Special use suffixes that never point at a public target.</summary>
    private static readonly string[] s_forbiddenHostSuffixes =
        [".localhost", ".local", ".internal", ".home.arpa"];

    /// <summary>Special use names, including cloud metadata endpoints reached by name.</summary>
    private static readonly string[] s_forbiddenHostNames =
        ["localhost", "local", "internal", "home.arpa", "metadata.google.internal", "instance-data"];

    /// <summary>
    /// Validates the shape of a target URL without touching the network.
    /// </summary>
    /// <param name="url">The candidate target URL.</param>
    /// <returns>A passing verdict from the <c>syntax</c> source, or a refusal naming the reason.
    /// Refusals use <see cref="UrlSafetyLevel.Blocked"/>: they are policy decisions, not
    /// reputation findings.</returns>
    public static UrlSafetyVerdict ValidateSyntax(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return UrlSafetyVerdict.Reject(UrlSafetyLevel.Blocked, SyntaxSource, "The target URL is empty.");
        }

        if (url.Length > MaxUrlLength)
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The target URL is longer than the permitted {MaxUrlLength} characters."));
        }

        foreach (char c in url)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return UrlSafetyVerdict.Reject(
                    UrlSafetyLevel.Blocked,
                    SyntaxSource,
                    "The target URL contains a control character or whitespace.");
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                "The target URL is not an absolute URL.");
        }

        if (!IsAllowedScheme(uri.Scheme))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                $"The scheme {uri.Scheme} is not allowed. Only http and https targets are accepted; "
                + "javascript, data, file, intent, vbscript, about and blob are refused outright.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                "The target URL embeds credentials (user:pass@host), which are used to disguise the real host.");
        }

        string host = uri.Host;

        if (string.IsNullOrEmpty(host))
        {
            return UrlSafetyVerdict.Reject(UrlSafetyLevel.Blocked, SyntaxSource, "The target URL has no host.");
        }

        // An IPv6 literal arrives bracketed, for example [::1].
        string literal = host.Trim('[', ']');

        if (IPAddress.TryParse(literal, out IPAddress? address))
        {
            return IsForbiddenAddress(address)
                ? UrlSafetyVerdict.Reject(
                    UrlSafetyLevel.Blocked,
                    AddressSource,
                    "The target URL points at a loopback, private, link local, carrier grade NAT, multicast, "
                    + "broadcast, documentation or cloud metadata address.")
                : UrlSafetyVerdict.Safe(SyntaxSource);
        }

        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                "The target URL contains a malformed IPv6 literal.");
        }

        if (!HostNormalizer.TryNormalize(host, out string normalizedHost))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                SyntaxSource,
                "The host of the target URL is not a valid hostname.");
        }

        if (IsForbiddenHost(normalizedHost))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                AddressSource,
                "The host of the target URL is a special use or internal name that never resolves to a public target.");
        }

        return UrlSafetyVerdict.Safe(SyntaxSource);
    }

    /// <summary>
    /// Decides whether an address may never be a link target: loopback, private, link local
    /// including the 169.254.169.254 cloud metadata endpoint, carrier grade NAT, multicast,
    /// broadcast, unspecified, the documentation ranges, and every IPv6 form that embeds an IPv4
    /// address.
    /// </summary>
    /// <param name="address">The address to judge.</param>
    /// <returns><see langword="true"/> when the address is forbidden. An address family other
    /// than IPv4 or IPv6 is forbidden as well: unknown means denied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static bool IsForbiddenAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsForbiddenIPv4(address),
            AddressFamily.InterNetworkV6 => IsForbiddenIPv6(address),
            _ => true,
        };
    }

    /// <summary>
    /// Decides whether a hostname may never be a link target.
    /// </summary>
    /// <param name="host">The hostname. It is lower cased and stripped of a trailing dot before
    /// the comparison, so passing an already normalized host is fine.</param>
    /// <returns><see langword="true"/> for <c>localhost</c> and anything under
    /// <c>.localhost</c>, <c>.local</c>, <c>.internal</c> or <c>.home.arpa</c>, for
    /// <c>metadata.google.internal</c> and <c>instance-data</c>, and for any single label host.
    /// The single label rule is what catches intranet names and the decimal form of an IPv4
    /// address, such as <c>2130706433</c> for 127.0.0.1.</returns>
    public static bool IsForbiddenHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        string value = host.Trim().Trim('[', ']').TrimEnd('.').ToLower(CultureInfo.InvariantCulture);

        if (value.Length == 0)
        {
            return true;
        }

        // A single label is an intranet name, and an intranet name is never a public target.
        // This also catches localhost, instance-data and numeric host forms.
        if (!value.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string name in s_forbiddenHostNames)
        {
            if (string.Equals(value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (string suffix in s_forbiddenHostSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedScheme(string scheme)
    {
        foreach (string allowed in AllowedSchemes)
        {
            if (string.Equals(scheme, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForbiddenIPv4(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];

        if (!address.TryWriteBytes(b, out int written) || written != 4)
        {
            return true;
        }

        return b[0] switch
        {
            0 => true,                              // 0.0.0.0/8, this network
            10 => true,                             // RFC 1918 private
            127 => true,                            // loopback
            100 => b[1] is >= 64 and <= 127,        // RFC 6598 carrier grade NAT 100.64/10
            169 => b[1] == 254,                     // link local, includes 169.254.169.254 metadata
            172 => b[1] is >= 16 and <= 31,         // RFC 1918 private
            192 => b[1] == 168                      // RFC 1918 private
                || (b[1] == 0 && b[2] == 0)         // IETF protocol assignments 192.0.0/24
                || (b[1] == 0 && b[2] == 2),        // TEST-NET-1 192.0.2/24
            198 => b[1] is 18 or 19                 // benchmarking 198.18/15
                || (b[1] == 51 && b[2] == 100),     // TEST-NET-2 198.51.100/24
            203 => b[1] == 0 && b[2] == 113,        // TEST-NET-3 203.0.113/24
            >= 224 => true,                         // multicast 224/4, reserved 240/4, broadcast
            _ => false,
        };
    }

    private static bool IsForbiddenIPv6(IPAddress address)
    {
        Span<byte> b = stackalloc byte[16];

        if (!address.TryWriteBytes(b, out int written) || written != 16)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsForbiddenAddress(address.MapToIPv4());
        }

        // IPv4 compatible form ::a.b.c.d — deprecated, and a classic filter bypass.
        if (IsAllZero(b[..12]))
        {
            uint embedded = BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
            return embedded == 0 || IsForbiddenAddress(new IPAddress(b.Slice(12, 4)));
        }

        // NAT64 well known prefix 64:ff9b::/96 embeds an IPv4 address in the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && IsAllZero(b[4..12]))
        {
            return IsForbiddenAddress(new IPAddress(b.Slice(12, 4)));
        }

        // 6to4 2002::/16 embeds the IPv4 address of the site in bytes 2 to 5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsForbiddenAddress(new IPAddress(b.Slice(2, 4)));
        }

        // Teredo 2001::/32 tunnels IPv4 in both directions; never a legitimate link target.
        if (address.IsIPv6Teredo)
        {
            return true;
        }

        // Documentation range 2001:db8::/32, the IPv6 counterpart of TEST-NET.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
        {
            return true;
        }

        return address.IsIPv6LinkLocal        // fe80::/10
            || address.IsIPv6UniqueLocal      // fc00::/7
            || address.IsIPv6SiteLocal        // fec0::/10, deprecated but still routed internally
            || address.IsIPv6Multicast;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
