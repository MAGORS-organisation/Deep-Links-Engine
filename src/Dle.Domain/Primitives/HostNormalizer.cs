namespace Dle.Domain.Primitives;

/// <summary>
/// Canonicalises a host name so that the same site is always looked up under the same cache key
/// and the same database row: lower case invariant, no scheme, no port, no trailing dot,
/// internationalised names converted to punycode and a leading <c>"www."</c> removed.
/// </summary>
/// <remarks>
/// Normalisation is a security boundary, not a convenience: two spellings of one host must never
/// resolve to two different tenants. Everything that survives normalisation is restricted to
/// <c>[a-z0-9.-]</c>, so no percent encoding, credential or bracketed literal can reach a store.
/// </remarks>
public static class HostNormalizer
{
    /// <summary>Maximum length of a normalised host name, as in RFC 1035.</summary>
    public const int MaxLength = 253;

    /// <summary>
    /// Punycode converter. The instance is configured once and only ever read, so sharing it
    /// across threads is safe.
    /// </summary>
    private static readonly IdnMapping Idn = new()
    {
        AllowUnassigned = false,

        // STD3 rules would reject characters that the final character filter rejects anyway,
        // but they also reject a leading digit in a label, which is legal in practice.
        UseStd3AsciiRules = false,
    };

    /// <summary>Characters that terminate the authority part of a URL.</summary>
    private static readonly char[] AuthorityTerminators = ['/', '?', '#'];

    /// <summary>
    /// Normalises a host name.
    /// </summary>
    /// <param name="host">The raw host, optionally carrying a scheme, credentials, a port or a path.</param>
    /// <returns>The normalised host.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value cannot be normalised into a valid host name.</exception>
    public static string Normalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!TryNormalize(host, out string normalized))
        {
            throw new ArgumentException("'" + host + "' is not a valid host name.", nameof(host));
        }

        return normalized;
    }

    /// <summary>
    /// Normalises a host name without throwing.
    /// </summary>
    /// <param name="host">The raw host, or <see langword="null"/>.</param>
    /// <param name="normalized">
    /// The normalised host, or <see cref="string.Empty"/> when the value is not a valid host.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the input is empty, longer than <see cref="MaxLength"/> after
    /// normalisation, not convertible to punycode, or contains a character outside <c>[a-z0-9.-]</c>.
    /// </returns>
    public static bool TryNormalize(string? host, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        ReadOnlySpan<char> work = host.AsSpan().Trim();

        // Scheme: "https://example.com" -> "example.com".
        int scheme = work.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            work = work[(scheme + 3)..];
        }

        // Credentials: "user:pass@example.com" -> "example.com".
        int at = work.LastIndexOf('@');

        if (at >= 0)
        {
            work = work[(at + 1)..];
        }

        // Path, query or fragment: "example.com/a?b#c" -> "example.com".
        int terminator = work.IndexOfAny(AuthorityTerminators);

        if (terminator >= 0)
        {
            work = work[..terminator];
        }

        // Port: "example.com:8443" -> "example.com". Only a purely numeric tail is a port, so a
        // bracketed IPv6 literal keeps its colons and is rejected by the character filter below.
        int colon = work.LastIndexOf(':');

        if (colon >= 0 && IsAllDigits(work[(colon + 1)..]))
        {
            work = work[..colon];
        }

        // Root label: "example.com." -> "example.com".
        work = work.TrimEnd('.').Trim();

        if (work.IsEmpty)
        {
            return false;
        }

        string ascii;

        try
        {
            // GetAscii lower cases the labels it converts; the explicit fold covers pure ASCII input.
            ascii = Idn.GetAscii(work.ToString().ToLowerInvariant()).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Empty label, disallowed code point or an over long label.
            return false;
        }

        // A single leading "www." is cosmetic and must not create a second identity.
        if (ascii.StartsWith("www.", StringComparison.Ordinal))
        {
            ascii = ascii[4..];
        }

        if (ascii.Length == 0 || ascii.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in ascii)
        {
            if (!IsAllowed(c))
            {
                return false;
            }
        }

        normalized = ascii;
        return true;
    }

    /// <summary>Characters permitted in a normalised host: <c>[a-z0-9.-]</c>.</summary>
    private static bool IsAllowed(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-';

    /// <summary>True when the span is non empty and consists only of ASCII digits.</summary>
    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
