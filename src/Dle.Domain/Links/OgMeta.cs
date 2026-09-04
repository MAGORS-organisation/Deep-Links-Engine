namespace Dle.Domain.Links;

/// <summary>
/// Open Graph and Twitter Card metadata rendered on the preview and interstitial pages.
/// </summary>
/// <remarks>
/// Social crawlers do not follow redirects reliably and do not run JavaScript, so a link shared on a
/// social network is only ever as good as the static HTML the edge returns to the crawler
/// (§A.2.6, ADR-009, FR-161). These values are that HTML's entire content.
/// </remarks>
public sealed record OgMeta
{
    /// <summary>Default value for <see cref="Type"/>.</summary>
    public const string DefaultType = "website";

    /// <summary>Default value for <see cref="TwitterCard"/>.</summary>
    public const string DefaultTwitterCard = "summary_large_image";

    /// <summary><c>og:title</c>.</summary>
    public string? Title { get; init; }

    /// <summary><c>og:description</c>.</summary>
    public string? Description { get; init; }

    /// <summary><c>og:image</c>. An absolute URL; relative values are not resolvable by a crawler.</summary>
    public string? ImageUrl { get; init; }

    /// <summary><c>og:site_name</c>.</summary>
    public string? SiteName { get; init; }

    /// <summary><c>og:type</c>. Defaults to <see cref="DefaultType"/>.</summary>
    public string? Type { get; init; }

    /// <summary><c>twitter:card</c>. Defaults to <see cref="DefaultTwitterCard"/>.</summary>
    public string? TwitterCard { get; init; }

    /// <summary>Metadata with nothing set beyond the two format defaults.</summary>
    public static OgMeta Empty { get; } = new()
    {
        Type = DefaultType,
        TwitterCard = DefaultTwitterCard,
    };

    /// <summary>
    /// Layers this metadata over a fallback, field by field: a value set here wins, an unset one is taken
    /// from <paramref name="fallback"/>, and anything still missing afterwards falls back to the format
    /// defaults. This is how a link inherits its domain's default Open Graph configuration.
    /// </summary>
    /// <param name="fallback">The lower-priority metadata, typically the link domain's default. May be <see langword="null"/>.</param>
    /// <returns>A new instance; neither operand is modified.</returns>
    public OgMeta MergeWith(OgMeta? fallback) => new()
    {
        Title = Coalesce(Title, fallback?.Title),
        Description = Coalesce(Description, fallback?.Description),
        ImageUrl = Coalesce(ImageUrl, fallback?.ImageUrl),
        SiteName = Coalesce(SiteName, fallback?.SiteName),
        Type = Coalesce(Type, fallback?.Type) ?? DefaultType,
        TwitterCard = Coalesce(TwitterCard, fallback?.TwitterCard) ?? DefaultTwitterCard,
    };

    private static string? Coalesce(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback) : primary;
}
