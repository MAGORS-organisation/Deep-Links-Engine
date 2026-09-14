using System.Collections.ObjectModel;

namespace Dle.Domain.Contracts;

/// <summary>Body of <c>POST /api/v1/links</c> (FR-101, FR-102, FR-104, FR-105).</summary>
public sealed record CreateLinkRequest
{
    /// <summary>Domain that will serve the link.</summary>
    public required Guid DomainId { get; init; }

    /// <summary>Custom slug. Omit to have one generated (8 base62 characters, ADR-007). A custom
    /// slug must be distinguishable from the generated shape, so 3 to 7 or 9 or more characters.</summary>
    public string? Slug { get; init; }

    /// <summary>Human readable title shown in the control plane.</summary>
    public string? Title { get; init; }

    /// <summary>Internal description.</summary>
    public string? Description { get; init; }

    /// <summary>Web fallback target used when no application can handle the link.</summary>
    public required string TargetUrl { get; init; }

    /// <summary>Path handed to the application, for example <c>/product/123</c>.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Routing rules. Must contain exactly one default rule and it must be last (TC-105).</summary>
    public IReadOnlyList<RoutingRule> RoutingRules { get; init; } = [];

    /// <summary>Open Graph metadata for crawler previews. Falls back to the domain default (FR-105).</summary>
    public OgMeta? Og { get; init; }

    /// <summary>UTM parameters appended to the web target.</summary>
    public IReadOnlyDictionary<string, string> Utm { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Campaign this link belongs to.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Free form tags used for filtering and search (FR-109).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Instant the link starts serving. Before it the link answers as not found.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>Instant the link stops serving (FR-104).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Where an expired link redirects instead of answering 404 (TC-104).</summary>
    public string? ExpiredUrl { get; init; }

    /// <summary>Whether the link is active on creation.</summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Body of <c>PATCH /api/v1/links/{id}</c>. Every member is optional; only the supplied ones change.
/// </summary>
public sealed record UpdateLinkRequest
{
    /// <summary>New title.</summary>
    public string? Title { get; init; }

    /// <summary>New description.</summary>
    public string? Description { get; init; }

    /// <summary>New web fallback target. Re-validated against the safety policy (§E.3).</summary>
    public string? TargetUrl { get; init; }

    /// <summary>New deep link path.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Replacement routing rule set. Validated as a whole, not merged.</summary>
    public IReadOnlyList<RoutingRule>? RoutingRules { get; init; }

    /// <summary>Replacement Open Graph metadata.</summary>
    public OgMeta? Og { get; init; }

    /// <summary>Replacement UTM parameters.</summary>
    public IReadOnlyDictionary<string, string>? Utm { get; init; }

    /// <summary>New campaign assignment.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Replacement tag list.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>New activation instant.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>New expiry instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>New expiry redirect target.</summary>
    public string? ExpiredUrl { get; init; }

    /// <summary>Activate or deactivate the link (FR-104).</summary>
    public bool? IsActive { get; init; }

    /// <summary>Note recorded in the link version history (FR-107).</summary>
    public string? ChangeNote { get; init; }
}

/// <summary>Representation of a link returned by the control plane.</summary>
public sealed record LinkResponse
{
    /// <summary>Link identifier as a string, because the value is a 64 bit Snowflake.</summary>
    public required string Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Serving domain.</summary>
    public required Guid DomainId { get; init; }

    /// <summary>Host the link is served from.</summary>
    public required string Host { get; init; }

    /// <summary>Slug within the domain.</summary>
    public required string Slug { get; init; }

    /// <summary>The full public URL, ready to paste.</summary>
    public required string ShortUrl { get; init; }

    /// <summary>URL of the QR image for this link (FR-106).</summary>
    public required string QrUrl { get; init; }

    /// <summary>Human readable title.</summary>
    public string? Title { get; init; }

    /// <summary>Internal description.</summary>
    public string? Description { get; init; }

    /// <summary>Web fallback target.</summary>
    public required string TargetUrl { get; init; }

    /// <summary>Deep link path handed to the application.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Routing rules in evaluation order.</summary>
    public IReadOnlyList<RoutingRule> RoutingRules { get; init; } = [];

    /// <summary>Open Graph metadata.</summary>
    public OgMeta? Og { get; init; }

    /// <summary>UTM parameters.</summary>
    public IReadOnlyDictionary<string, string> Utm { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Campaign assignment.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Whether the link is active.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Activation instant.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>Expiry instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Expiry redirect target.</summary>
    public string? ExpiredUrl { get; init; }

    /// <summary>Set when the link is quarantined after an abuse report (TC-103).</summary>
    public DateTimeOffset? QuarantinedAt { get; init; }

    /// <summary>Version counter, incremented on every change (FR-107).</summary>
    public required int Version { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Instant of the last change.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A page of results plus the cursor needed to fetch the next one.</summary>
/// <typeparam name="T">Item type carried by the page.</typeparam>
public sealed record PagedResponse<T>
{
    /// <summary>The items on this page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Opaque cursor for the next page, or <see langword="null"/> at the end.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total matching items when the store can supply it cheaply.</summary>
    public long? Total { get; init; }
}

/// <summary>
/// One line of a bulk create request. Streamed as NDJSON so a ten thousand row batch never has to
/// be buffered whole (FR-103).
/// </summary>
public sealed record BulkLinkItem
{
    /// <summary>Caller supplied correlation key, echoed back in the result line.</summary>
    public string? Ref { get; init; }

    /// <summary>The link to create.</summary>
    public required CreateLinkRequest Link { get; init; }
}

/// <summary>Result of one line of a bulk create request.</summary>
public sealed record BulkLinkResult
{
    /// <summary>Correlation key from the request line.</summary>
    public string? Ref { get; init; }

    /// <summary>Whether the line succeeded.</summary>
    public required bool Ok { get; init; }

    /// <summary>Created link identifier when <see cref="Ok"/> is true.</summary>
    public string? Id { get; init; }

    /// <summary>Short URL when <see cref="Ok"/> is true.</summary>
    public string? ShortUrl { get; init; }

    /// <summary>Problem type from <see cref="ProblemCodes"/> when the line failed.</summary>
    public string? Error { get; init; }

    /// <summary>Human readable failure detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Query for <c>GET /api/v1/links/{id}/simulate</c>. Answers "what happens for an iPhone 15 on
/// iOS 18 in Slovakia, opened from Instagram" without generating a click event (FR-129).
/// </summary>
public sealed record SimulateRequest
{
    /// <summary>User agent string to classify. Alternative to the explicit fields below.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Platform override: <c>ios</c>, <c>android</c>, <c>desktop</c> or <c>other</c>.</summary>
    public string? Platform { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string? Country { get; init; }

    /// <summary>Region within the country.</summary>
    public string? Region { get; init; }

    /// <summary>Primary language subtag, for example <c>sk</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Operating system version.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Host application version.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Channel name, for example <c>in_app_ig</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>Instant to evaluate time windows against. Defaults to now.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Click identifier used for deterministic A/B bucketing. Defaults to a fixed sample
    /// value so repeated simulations are reproducible (FR-125).</summary>
    public string? ClickId { get; init; }
}

/// <summary>Outcome of a rule simulation, including why each rule did or did not match.</summary>
public sealed record SimulateResponse
{
    /// <summary>Identifier of the rule that won.</summary>
    public required string MatchedRuleId { get; init; }

    /// <summary>Resulting decision kind, for example <c>interstitial</c> or <c>store</c>.</summary>
    public required string Decision { get; init; }

    /// <summary>Web URL that would be produced, when the decision leads to one.</summary>
    public string? Url { get; init; }

    /// <summary>Deep link path that would be handed to the application.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Store URL that would be produced, including the referrer parameter on Android.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>A/B bucket derived from the click identifier.</summary>
    public short? AbBucket { get; init; }

    /// <summary>A/B variant that was selected.</summary>
    public string? AbVariant { get; init; }

    /// <summary>Effective consent mode applied during evaluation.</summary>
    public required string ConsentMode { get; init; }

    /// <summary>Per rule explanation in evaluation order, so a marketer can see why rule 2 lost.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];
}

/// <summary>One historical version of a link (FR-107).</summary>
public sealed record LinkVersionResponse
{
    /// <summary>Version number.</summary>
    public required int Version { get; init; }

    /// <summary>Who made the change.</summary>
    public Guid? ChangedBy { get; init; }

    /// <summary>When the change was made.</summary>
    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>Note supplied with the change.</summary>
    public string? ChangeNote { get; init; }

    /// <summary>The link as it looked at this version.</summary>
    public LinkResponse? Snapshot { get; init; }
}
