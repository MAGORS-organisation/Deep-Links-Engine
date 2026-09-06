using System.Text;

namespace Dle.Domain.Primitives;

/// <summary>
/// Rules for the path segment that identifies a link. A generated slug is exactly
/// <see cref="GeneratedLength"/> base62 characters (ADR-007); a custom slug must be
/// distinguishable from that shape, so it is either shorter or longer, never exactly as long.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryNormalize"/> is the homoglyph defence required by TC-109. Characters outside
/// <c>[a-z0-9_-]</c> are <em>rejected</em>, never folded: a Cyrillic <c>а</c> (U+0430) must not
/// become a Latin <c>a</c>, otherwise a tenant could mint a slug that collides with another
/// tenant's link on the same domain.
/// </para>
/// <para>
/// Compatibility normalisation (NFKC) runs first, so full width and ligature spellings of characters
/// that <em>are</em> allowed collapse to their canonical ASCII form before validation. The product
/// builds with <c>InvariantGlobalization</c> enabled, where
/// <see cref="string.Normalize(System.Text.NormalizationForm)"/> is a silent no-op, so the fold is
/// carried out by <see cref="CompatibilityFold"/> rather than by the runtime. The outcome is
/// therefore identical with and without ICU.
/// </para>
/// </remarks>
public static class SlugPolicy
{
    /// <summary>Length of a generated slug: 8 base62 characters, about 47.6 bits (ADR-007).</summary>
    public const int GeneratedLength = 8;

    /// <summary>Shortest custom slug that is accepted.</summary>
    public const int MinCustomLength = 3;

    /// <summary>Longest slug that is accepted, generated or custom.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Path segments that the edge serves itself and that therefore can never be a link slug.
    /// </summary>
    public static readonly string[] Reserved =
    [
        ".well-known",
        "api",
        "healthz",
        "readyz",
        "abuse",
        "_dl",
        "favicon.ico",
        "robots.txt",
        "static",
        "assets",
    ];

    /// <summary>
    /// Normalises a raw slug: NFKC compatibility form, trimmed, lower case invariant, then
    /// validated against <c>[a-z0-9_-]</c>.
    /// </summary>
    /// <param name="raw">The slug as it arrived from the request path or from the API.</param>
    /// <param name="slug">The normalised slug, or <see cref="string.Empty"/> on rejection.</param>
    /// <returns>
    /// <see langword="false"/> when the value is empty, longer than <see cref="MaxLength"/>, or
    /// contains any character outside <c>[a-z0-9_-]</c> after normalisation. Length rules that
    /// separate custom from generated slugs are not applied here; see <see cref="IsValidCustom"/>.
    /// </returns>
    public static bool TryNormalize(string? raw, out string slug)
    {
        slug = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string normalized;

        try
        {
            normalized = raw.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Unpaired surrogate or otherwise invalid UTF-16.
            return false;
        }

        // Under InvariantGlobalization the call above returned `raw` untouched. Fold the
        // compatibility characters that matter to this allowlist ourselves so the NFKC step
        // SHARED-KERNEL §1 requires happens whether or not the runtime carries ICU.
        normalized = CompatibilityFold.Apply(normalized);

        normalized = normalized.Trim().ToLowerInvariant();

        if (normalized.Length == 0 || normalized.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in normalized)
        {
            if (!IsAllowed(c))
            {
                return false;
            }
        }

        slug = normalized;
        return true;
    }

    /// <summary>
    /// Decides whether a normalised slug may be stored as a user chosen slug.
    /// </summary>
    /// <param name="slug">The slug, normally the output of <see cref="TryNormalize"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the slug is between <see cref="MinCustomLength"/> and
    /// <see cref="MaxLength"/> characters, uses only <c>[a-z0-9_-]</c>, is not
    /// <see cref="IsReserved">reserved</see> and does not
    /// <see cref="LooksGenerated">look generated</see>.
    /// </returns>
    public static bool IsValidCustom(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length < MinCustomLength || slug.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in slug)
        {
            if (!IsAllowed(c))
            {
                return false;
            }
        }

        return !IsReserved(slug) && !LooksGenerated(slug);
    }

    /// <summary>
    /// Tells whether a slug collides with a path the platform serves itself.
    /// </summary>
    /// <param name="slug">The slug or path segment to test.</param>
    /// <returns><see langword="true"/> when the value is one of <see cref="Reserved"/>, ignoring case.</returns>
    public static bool IsReserved(string slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return false;
        }

        foreach (string reserved in Reserved)
        {
            if (string.Equals(slug, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells whether a slug has the shape produced by the keyed permutation: exactly
    /// <see cref="GeneratedLength"/> characters, all of them base62.
    /// </summary>
    /// <param name="slug">The slug to test.</param>
    /// <returns><see langword="true"/> when the slug is indistinguishable from a generated one.</returns>
    public static bool LooksGenerated(string slug)
    {
        if (slug is null || slug.Length != GeneratedLength)
        {
            return false;
        }

        foreach (char c in slug)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Characters permitted in a normalised slug: <c>[a-z0-9_-]</c>.</summary>
    private static bool IsAllowed(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-';
}
