using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Dle.Domain.Crypto;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Builds and checks the two headers a delivery carries (§B.7.4, §E.5.3, T-13, TC-165).
/// </summary>
/// <remarks>
/// <para>
/// Two signatures over the same bytes, in two slots, for two different readers.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>v1</c> is HMAC-SHA-256 under the secret the subscription was created with. It exists
///     because it is trivial to verify: three lines in any language, no key distribution, no
///     dependency. That is what makes it the slot a customer actually uses on the first day.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>v2</c> is Ed25519 under a key published at <c>/.well-known/jwks.json</c>. It exists
///     because the symmetric slot cannot prove anything to a third party: anyone able to verify an
///     HMAC is equally able to forge one. A fraud team, an auditor, or a downstream partner can
///     verify <c>v2</c> without ever being given the shared secret.
///     </description>
///   </item>
/// </list>
/// <para>
/// The second slot is also the migration path §E.5.3 lays out. Adding <c>v3=&lt;ML-DSA-65&gt;</c>
/// later is a new member in an already multi-valued header, which every correct receiver already
/// ignores when it does not recognise it — so a post-quantum signature lands without a breaking
/// change for a single integrator. That is the whole reason the header is shaped this way on day
/// one rather than being retrofitted.
/// </para>
/// <para>
/// <b>What is signed.</b> The signing input is the ASCII decimal Unix timestamp, a full stop, and
/// then the response body <em>exactly as transmitted</em>: <c>t + "." + body</c>. The body bytes
/// are the bytes stored in the outbox and handed to the HTTP client unchanged, so a retry
/// reproduces them byte for byte and a modified body fails verification (TC-165). Binding the
/// timestamp into the signed material is what stops a captured delivery from being replayed with a
/// fresh <c>t</c>: rewriting it invalidates both signatures.
/// </para>
/// </remarks>
public static class WebhookSignature
{
    /// <summary>Name of the header carrying the timestamp, the signatures and the key identifier.</summary>
    public const string SignatureHeader = "DLE-Signature";

    /// <summary>Name of the header naming the algorithms in the slots.</summary>
    public const string AlgorithmHeader = "DLE-Alg";

    /// <summary>Value of <see cref="AlgorithmHeader"/> for the two slot signature of §B.7.4.</summary>
    public const string AlgorithmValue = "HS256+Ed25519";

    /// <summary>Separator between the timestamp and the body in the signing input.</summary>
    private const byte Separator = (byte)'.';

    /// <summary>
    /// Builds the value of <see cref="SignatureHeader"/> for one delivery.
    /// </summary>
    /// <param name="body">The exact bytes that will be transmitted as the request body.</param>
    /// <param name="secret">The subscription's shared secret, in the clear.</param>
    /// <param name="signer">Signer for the asymmetric slot, or <see langword="null"/> when the
    /// deployment publishes no webhook key. The <c>v2</c> member is then omitted rather than
    /// filled with something that cannot be verified.</param>
    /// <param name="timestamp">The instant the delivery is being sent.</param>
    /// <returns>The header value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    public static string Create(
        ReadOnlySpan<byte> body,
        byte[] secret,
        ISigner? signer,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(secret);

        long unixSeconds = timestamp.ToUnixTimeSeconds();
        byte[] signingInput = BuildSigningInput(unixSeconds, body);

        Span<byte> hmac = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, signingInput, hmac);

        StringBuilder builder = new(160);

        builder.Append("t=")
            .Append(unixSeconds.ToString(CultureInfo.InvariantCulture))
            .Append(", v1=")
            .Append(Convert.ToBase64String(hmac));

        if (signer is not null)
        {
            builder.Append(", v2=")
                .Append(Convert.ToBase64String(signer.Sign(signingInput)))
                .Append(", kid=")
                .Append(signer.KeyId);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Verifies a received header the way a correct receiver would.
    /// </summary>
    /// <param name="headerValue">Value of <see cref="SignatureHeader"/> as received.</param>
    /// <param name="body">The received body bytes.</param>
    /// <param name="secret">The shared secret of the subscription.</param>
    /// <param name="now">The receiver's current instant.</param>
    /// <param name="tolerance">How stale the timestamp may be (§B.7.4: five minutes).</param>
    /// <returns><see langword="true"/> only when the timestamp is fresh and the <c>v1</c> slot
    /// verifies over exactly these bytes.</returns>
    /// <remarks>
    /// <para>
    /// Shipped as product code rather than as a test helper because it is the reference the
    /// documentation points at, and because the contract tests for TC-165 have to check the
    /// signature the engine actually produces rather than a second implementation that might drift
    /// from it.
    /// </para>
    /// <para>
    /// The comparison is <see cref="CryptographicOperations.FixedTimeEquals"/>. An equality
    /// comparison on a MAC leaks it one byte at a time to anyone who can measure the response
    /// (SHARED-KERNEL §17.6, T-17).
    /// </para>
    /// </remarks>
    public static bool VerifySymmetric(
        string? headerValue,
        ReadOnlySpan<byte> body,
        byte[] secret,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (!TryParse(headerValue, out WebhookSignatureHeader parsed))
        {
            return false;
        }

        DateTimeOffset signedAt = DateTimeOffset.FromUnixTimeSeconds(parsed.UnixSeconds);

        if (signedAt > now + tolerance || signedAt < now - tolerance)
        {
            return false;
        }

        if (parsed.V1 is not { } presented)
        {
            return false;
        }

        byte[] signingInput = BuildSigningInput(parsed.UnixSeconds, body);

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, signingInput, expected);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// Parses a received header into its members.
    /// </summary>
    /// <param name="headerValue">Value of <see cref="SignatureHeader"/>.</param>
    /// <param name="header">The parsed members.</param>
    /// <returns><see langword="true"/> when the header carries a timestamp and at least one
    /// signature slot.</returns>
    /// <remarks>
    /// Unknown members are ignored rather than refused, which is what makes adding <c>v3</c> a
    /// non-breaking change (§E.5.3).
    /// </remarks>
    public static bool TryParse(string? headerValue, out WebhookSignatureHeader header)
    {
        header = default;

        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > 4096)
        {
            return false;
        }

        long unixSeconds = 0;
        bool hasTimestamp = false;
        byte[]? v1 = null;
        byte[]? v2 = null;
        string? kid = null;

        foreach (string part in headerValue.Split(','))
        {
            ReadOnlySpan<char> member = part.AsSpan().Trim();
            int equals = member.IndexOf('=');

            if (equals <= 0 || equals == member.Length - 1)
            {
                continue;
            }

            ReadOnlySpan<char> name = member[..equals].Trim();
            ReadOnlySpan<char> value = member[(equals + 1)..].Trim();

            if (name.Equals("t", StringComparison.Ordinal))
            {
                hasTimestamp = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds);
            }
            else if (name.Equals("v1", StringComparison.Ordinal))
            {
                v1 = DecodeBase64(value);
            }
            else if (name.Equals("v2", StringComparison.Ordinal))
            {
                v2 = DecodeBase64(value);
            }
            else if (name.Equals("kid", StringComparison.Ordinal))
            {
                kid = value.ToString();
            }
        }

        if (!hasTimestamp || (v1 is null && v2 is null))
        {
            return false;
        }

        header = new WebhookSignatureHeader(unixSeconds, v1, v2, kid);
        return true;
    }

    /// <summary>Builds the bytes both slots sign: <c>t + "." + body</c>.</summary>
    /// <param name="unixSeconds">The timestamp carried in the header.</param>
    /// <param name="body">The transmitted body.</param>
    /// <returns>The signing input.</returns>
    private static byte[] BuildSigningInput(long unixSeconds, ReadOnlySpan<byte> body)
    {
        Span<byte> timestamp = stackalloc byte[20];

        if (!Utf8Formatter.TryFormat(unixSeconds, timestamp, out int written))
        {
            // Cannot happen: twenty bytes hold every long. Fail closed rather than sign a
            // truncated timestamp (SHARED-KERNEL §17.9).
            throw new InvalidOperationException("The delivery timestamp could not be formatted.");
        }

        byte[] input = new byte[written + 1 + body.Length];

        timestamp[..written].CopyTo(input);
        input[written] = Separator;
        body.CopyTo(input.AsSpan(written + 1));

        return input;
    }

    /// <summary>Decodes one base64 member, treating anything malformed as absent.</summary>
    private static byte[]? DecodeBase64(ReadOnlySpan<char> value)
    {
        byte[] buffer = new byte[((value.Length + 3) / 4) * 3];

        return Convert.TryFromBase64Chars(value, buffer, out int written)
            ? buffer.AsSpan(0, written).ToArray()
            : null;
    }
}

/// <summary>
/// The members of a parsed <c>DLE-Signature</c> header.
/// </summary>
/// <param name="UnixSeconds">Value of <c>t</c>: when the delivery was signed.</param>
/// <param name="V1">Value of <c>v1</c>: the HMAC-SHA-256 slot, or <see langword="null"/>.</param>
/// <param name="V2">Value of <c>v2</c>: the Ed25519 slot, or <see langword="null"/>.</param>
/// <param name="Kid">Value of <c>kid</c>: which published key verifies <paramref name="V2"/>.</param>
public readonly record struct WebhookSignatureHeader(long UnixSeconds, byte[]? V1, byte[]? V2, string? Kid);
