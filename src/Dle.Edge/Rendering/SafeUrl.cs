namespace Dle.Edge.Rendering;

/// <summary>
/// Scheme allow-listing for every URL that reaches an <c>href</c> or <c>src</c> attribute.
/// </summary>
/// <remarks>
/// <para>
/// T-11 is stored cross-site scripting through the values a page interpolates: Open Graph metadata,
/// the link's own target, tenant branding and anything arriving on the query string. HTML encoding
/// handles the text; it does not handle a URL, because a <c>javascript:</c> URL survives encoding
/// intact and executes on click. The scheme has to be checked separately, and it has to be checked
/// with an allow list - a deny list of the schemes known today is a promise about the schemes invented
/// tomorrow.
/// </para>
/// <para>
/// Custom application schemes are the one exception, and a narrow one. The interstitial's button may
/// carry <c>myapp://…</c>, but only when the scheme matches the value configured on the link, and never
/// a scheme the browser treats as executable or as a document (§A.2.3, CVE-2026-26123).
/// </para>
/// </remarks>
internal static class SafeUrl
{
    /// <summary>Longest URL that will be written into an attribute.</summary>
    internal const int MaxLength = 2048;

    private static readonly string[] DangerousSchemes =
    [
        "javascript",
        "data",
        "vbscript",
        "file",
        "blob",
        "about",
        "filesystem",
        "intent",
        "jar",
        "view-source",
    ];

    private static readonly string[] AllowedImagePrefixes =
    [
        "data:image/png;base64,",
        "data:image/jpeg;base64,",
        "data:image/webp;base64,",
        "data:image/gif;base64,",
        "data:image/svg+xml;base64,",
    ];

    /// <summary>
    /// Returns the URL when it is an absolute <c>http</c> or <c>https</c> URL of sane length, and
    /// <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="url">Candidate URL, from configuration, the database or a crawler-visible field.</param>
    /// <returns>The URL, or <see langword="null"/>.</returns>
    internal static string? Web(string? url)
    {
        if (!IsSane(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
               (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ? url
            : null;
    }

    /// <summary>
    /// Returns the URL when it is an absolute <c>https</c> URL. Used for anything the browser fetches
    /// as a subresource, where a mixed-content downgrade is silent.
    /// </summary>
    /// <param name="url">Candidate URL.</param>
    /// <returns>The URL, or <see langword="null"/>.</returns>
    internal static string? Https(string? url)
    {
        if (!IsSane(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
               parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    /// <summary>
    /// Returns the URL when it is an absolute <c>http</c>/<c>https</c> URL, or a custom-scheme URL whose
    /// scheme is exactly one of <paramref name="allowedCustomSchemes"/>.
    /// </summary>
    /// <param name="url">Candidate URL, typically produced by <c>RoutingUrlBuilder.BuildDeeplinkUrl</c>.</param>
    /// <param name="allowedCustomSchemes">
    /// Schemes configured on the link, without the separator. Entries that are empty or that name a
    /// dangerous scheme are ignored.
    /// </param>
    /// <returns>The URL, or <see langword="null"/>.</returns>
    internal static string? Deeplink(string? url, ReadOnlySpan<string?> allowedCustomSchemes)
    {
        string? web = Web(url);

        if (web is not null)
        {
            return web;
        }

        if (!IsSane(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return null;
        }

        foreach (string? allowed in allowedCustomSchemes)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            string candidate = allowed.Trim();

            if (IsDangerous(candidate) || !IsWellFormedScheme(candidate))
            {
                continue;
            }

            if (parsed.Scheme.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        // Nothing matched: the branch defaults to refusing the URL (SHARED-KERNEL §17.9).
        return null;
    }

    /// <summary>
    /// Returns a <c>mailto:</c> URL for an address that looks like an address, and
    /// <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="address">Mailbox from configuration.</param>
    /// <returns>The <c>mailto:</c> URL, or <see langword="null"/>.</returns>
    internal static string? MailTo(string? address)
    {
        const int maxAddressLength = 254;

        if (string.IsNullOrWhiteSpace(address) || address.Length > maxAddressLength)
        {
            return null;
        }

        string trimmed = address.Trim();
        int at = trimmed.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || IsForbiddenInAddress(c))
            {
                return null;
            }
        }

        return string.Concat("mailto:", trimmed);
    }

    /// <summary>
    /// Returns a <c>data:</c> URI when it carries a base64 image of a format a browser renders inside an
    /// SVG, and <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="dataUri">Candidate URI, from tenant branding.</param>
    /// <returns>The URI, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A QR logo cannot be an external URL: the SVG is served as an image, and browsers refuse to load
    /// subresources from inside an image document. It must not be one either, because fetching a
    /// tenant-supplied URL server-side would be a third-party call on a cached edge path (NFR-14) and an
    /// SSRF primitive. Inline base64 is the only shape that is both renderable and safe.
    /// </remarks>
    internal static string? ImageDataUri(string? dataUri)
    {
        const int maxDataUriLength = 64 * 1024;

        if (string.IsNullOrWhiteSpace(dataUri) || dataUri.Length > maxDataUriLength)
        {
            return null;
        }

        foreach (string prefix in AllowedImagePrefixes)
        {
            if (!dataUri.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<char> payload = dataUri.AsSpan(prefix.Length);

            if (payload.IsEmpty)
            {
                return null;
            }

            foreach (char c in payload)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '='))
                {
                    return null;
                }
            }

            return dataUri;
        }

        return null;
    }

    /// <summary>
    /// Returns a colour when it is a three or six digit hexadecimal literal, and <see langword="null"/>
    /// otherwise.
    /// </summary>
    /// <param name="color">Candidate colour, from tenant branding.</param>
    /// <returns>The colour, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Branding colours are interpolated into a nonce'd inline stylesheet, which makes an unvalidated
    /// value a CSS injection: a closing brace and a new rule is enough to restyle the page, hide the
    /// real button and put an attacker's in its place. Only the literal shape is accepted; named colours
    /// and functional notations are not.
    /// </remarks>
    internal static string? HexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        string trimmed = color.Trim();

        if (trimmed.Length is not (4 or 7) || trimmed[0] != '#')
        {
            return null;
        }

        for (int i = 1; i < trimmed.Length; i++)
        {
            if (!char.IsAsciiHexDigit(trimmed[i]))
            {
                return null;
            }
        }

        return trimmed;
    }

    private static bool IsForbiddenInAddress(char c) =>
        c is '<' or '>' or '"' or '\'' or ':' or '/' or '\\' or ',' or ';' or '?' or '#' or '&' or '%';

    private static bool IsSane(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Length <= MaxLength &&
        !ContainsControlCharacter(url);

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDangerous(string scheme)
    {
        foreach (string dangerous in DangerousSchemes)
        {
            if (scheme.Equals(dangerous, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWellFormedScheme(string scheme)
    {
        const int maxSchemeLength = 32;

        if (scheme.Length is 0 or > maxSchemeLength || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
