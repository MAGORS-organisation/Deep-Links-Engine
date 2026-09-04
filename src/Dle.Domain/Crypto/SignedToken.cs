namespace Dle.Domain.Crypto;

/// <summary>
/// A token in the engine format <c>dlt1.&lt;alg&gt;.&lt;kid&gt;.&lt;base64url(payload)&gt;.&lt;base64url(signature)&gt;</c>
/// (§E.4.2).
/// </summary>
/// <remarks>
/// <para>
/// The version prefix, the algorithm and the key identifier are all in the token from the first
/// release. That is the whole of the crypto agility promise: adding any of them later would break
/// every integrator already parsing tokens.
/// </para>
/// <para>
/// This type is a parser and a formatter, nothing more. It never decodes the payload and never
/// checks the signature, so parsing an untrusted token cannot fail dangerously; the caller hands
/// the parts to an <see cref="IVerifier"/>, and only then may the payload be believed.
/// </para>
/// </remarks>
/// <param name="Alg">Algorithm identifier, one of the constants on <see cref="SignatureAlgorithms"/>.</param>
/// <param name="Kid">Key identifier, which allows rotation without an outage.</param>
/// <param name="Payload">Base64url encoded payload carrying at least <c>iat</c>, <c>exp</c>,
/// <c>jti</c> and <c>aud</c>.</param>
/// <param name="Signature">Base64url encoded signature over the payload.</param>
public sealed record SignedToken(string Alg, string Kid, string Payload, string Signature)
{
    /// <summary>Format version prefix. A token that does not start with it is not ours.</summary>
    public const string Prefix = "dlt1";

    private const int SegmentCount = 5;

    private static readonly SignedToken s_empty =
        new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>Renders the token in its wire format.</summary>
    /// <returns>The five dot separated segments, starting with <see cref="Prefix"/>.</returns>
    public override string ToString() => string.Join('.', Prefix, Alg, Kid, Payload, Signature);

    /// <summary>
    /// Parses a token. Structural validation only: the value is rejected unless it has exactly
    /// five dot separated segments, the first is <see cref="Prefix"/>, none is empty, and the
    /// last two are syntactically base64url.
    /// </summary>
    /// <param name="value">The token text, or <see langword="null"/>.</param>
    /// <param name="token">The parsed token on success. On failure it is a placeholder with empty
    /// segments, which must not be used; check the return value.</param>
    /// <returns><see langword="true"/> when the token is well formed. This method never throws,
    /// because it runs on untrusted input on the request path.</returns>
    public static bool TryParse(string? value, out SignedToken token)
    {
        token = s_empty;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] parts = value.Split('.');

        if (parts.Length != SegmentCount)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                return false;
            }
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsBase64Url(parts[3]) || !IsBase64Url(parts[4]))
        {
            return false;
        }

        token = new SignedToken(parts[1], parts[2], parts[3], parts[4]);
        return true;
    }

    /// <summary>
    /// Checks the base64url alphabet without decoding. Padding is rejected: the format is
    /// unpadded, and accepting both forms would make one token expressible in two ways, which
    /// breaks replay detection keyed on the token text.
    /// </summary>
    private static bool IsBase64Url(string value)
    {
        // A base64 group can leave 0, 2 or 3 characters over. One leftover character is
        // impossible, so it is a reliable sign of truncation.
        if (value.Length % 4 == 1)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
