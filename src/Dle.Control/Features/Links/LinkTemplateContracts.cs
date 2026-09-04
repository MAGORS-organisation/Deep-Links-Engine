namespace Dle.Control.Features.Links;

/// <summary>
/// Body of <c>POST</c> and <c>PATCH /api/v1/links/templates/{id}</c> (FR-108).
/// </summary>
/// <remarks>
/// <para>
/// A template is a campaign. §B.5.2 gives <c>campaigns</c> one JSON column, so the template document
/// lives in it: the UTM map the column is named for, plus the routing rules and the target defaults
/// that FR-108 asks a campaign to prefill. A row written by an older version that holds a flat string
/// map is still read correctly, as a template with UTM defaults and nothing else.
/// </para>
/// <para>
/// Nothing here is validated as strictly as a link, because a template is not served: the rules are
/// checked when a link is created from it, against the same validator every other link passes
/// through (TC-105). A half-finished template is a legitimate intermediate state; a half-finished
/// link is not.
/// </para>
/// </remarks>
public sealed record LinkTemplateRequest
{
    /// <summary>Template name, unique within the tenant by convention rather than by constraint.</summary>
    public required string Name { get; init; }

    /// <summary>UTM parameters copied into links created from this template.</summary>
    public IReadOnlyDictionary<string, string> Utm { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Routing rules copied into a link that supplies none of its own.</summary>
    public IReadOnlyList<RoutingRule> RoutingRules { get; init; } = [];

    /// <summary>Default web fallback target for links created from this template.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Default deep link path.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Default Open Graph metadata (FR-105).</summary>
    public OgMeta? Og { get; init; }

    /// <summary>Tags added to every link created from this template (FR-109).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Representation of a link template (FR-108).</summary>
public sealed record LinkTemplateResponse
{
    /// <summary>Template identifier. It is also the campaign identifier a link refers to.</summary>
    public required Guid Id { get; init; }

    /// <summary>Template name.</summary>
    public required string Name { get; init; }

    /// <summary>UTM parameters.</summary>
    public IReadOnlyDictionary<string, string> Utm { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Routing rules.</summary>
    public IReadOnlyList<RoutingRule> RoutingRules { get; init; } = [];

    /// <summary>Default web fallback target.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Default deep link path.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Default Open Graph metadata.</summary>
    public OgMeta? Og { get; init; }

    /// <summary>Tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
