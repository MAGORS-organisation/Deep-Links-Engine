using System.Collections.ObjectModel;

namespace Dle.Domain.Contracts;

/// <summary>Body of <c>POST /api/v1/tenants</c> (FR-241).</summary>
public sealed record CreateTenantRequest
{
    /// <summary>Short unique identifier used in URLs and logs.</summary>
    public required string Slug { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Consent mode: <c>off</c>, <c>aggregate_only</c> or <c>full</c>. Defaults to
    /// <c>aggregate_only</c>, the mode that needs no consent banner (§E.6.2, FR-248).</summary>
    public string? ConsentMode { get; init; }
}

/// <summary>Representation of a tenant.</summary>
public sealed record TenantResponse
{
    /// <summary>Tenant identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Short unique identifier.</summary>
    public required string Slug { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Lifecycle status: <c>active</c>, <c>suspended</c> or <c>deleted</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Effective consent mode.</summary>
    public required string ConsentMode { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Body of <c>POST /api/v1/api-keys</c> (FR-242).</summary>
public sealed record CreateApiKeyRequest
{
    /// <summary>Human readable name, so a key can be identified without revealing it.</summary>
    public required string Name { get; init; }

    /// <summary>Role: <c>owner</c>, <c>admin</c>, <c>editor</c> or <c>viewer</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Optional narrower scopes within the role.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Expiry instant. Keys without one never expire, which is discouraged.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Response to key creation. The secret appears here and nowhere else: only an Argon2id hash is
/// stored, so a lost key is replaced, never recovered (FR-242, K5 in §E.4.1).
/// </summary>
public sealed record ApiKeyCreatedResponse
{
    /// <summary>Key identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Human readable name.</summary>
    public required string Name { get; init; }

    /// <summary>The secret, shown exactly once.</summary>
    public required string Secret { get; init; }

    /// <summary>Non secret prefix, used to identify the key in listings and logs.</summary>
    public required string Prefix { get; init; }

    /// <summary>Assigned role.</summary>
    public required string Role { get; init; }

    /// <summary>Expiry instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Representation of an API key, without the secret.</summary>
public sealed record ApiKeyResponse
{
    /// <summary>Key identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Human readable name.</summary>
    public required string Name { get; init; }

    /// <summary>Non secret prefix.</summary>
    public required string Prefix { get; init; }

    /// <summary>Assigned role.</summary>
    public required string Role { get; init; }

    /// <summary>Granted scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>When the key was last used, for spotting keys that can be retired.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Expiry instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Revocation instant, when the key has been revoked.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Query parameters accepted by the analytics endpoints (FR-202).</summary>
public sealed record AnalyticsQueryRequest
{
    /// <summary>Start of the window, inclusive.</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>End of the window, exclusive.</summary>
    public required DateTimeOffset To { get; init; }

    /// <summary>Bucket size: <c>hour</c>, <c>day</c>, <c>week</c> or <c>month</c>.</summary>
    public string? Grain { get; init; }

    /// <summary>Restrict to one link.</summary>
    public string? LinkId { get; init; }

    /// <summary>Restrict to one campaign.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Restrict to one country.</summary>
    public string? Country { get; init; }

    /// <summary>Restrict to one platform.</summary>
    public string? Platform { get; init; }

    /// <summary>Include bot traffic. Off by default, because bot clicks must not inflate campaign
    /// numbers (FR-205, TC-106).</summary>
    public bool IncludeBots { get; init; }

    /// <summary>Maximum rows returned by a breakdown.</summary>
    public int? Limit { get; init; }
}

/// <summary>A time series plus the totals for the same window.</summary>
public sealed record TimeSeriesResponse
{
    /// <summary>Bucket size actually applied.</summary>
    public required string Grain { get; init; }

    /// <summary>The buckets, oldest first.</summary>
    public IReadOnlyList<TimeSeriesPoint> Points { get; init; } = [];

    /// <summary>Totals across the window.</summary>
    public FunnelSummary? Totals { get; init; }
}

/// <summary>A breakdown of one dimension over the queried window.</summary>
public sealed record BreakdownResponse
{
    /// <summary>Dimension that was grouped by, for example <c>country</c> or <c>channel</c>.</summary>
    public required string Dimension { get; init; }

    /// <summary>The rows, most significant first.</summary>
    public IReadOnlyList<BreakdownRow> Rows { get; init; } = [];
}

/// <summary>
/// The honest attribution split. This is the number commercial measurement partners systematically
/// blur, and stating it plainly is the product argument, not a footnote (ADR-008, §0.2).
/// </summary>
public sealed record AttributionQualityResponse
{
    /// <summary>Attributed installs whose match is deterministic, confidence exactly 1.00.</summary>
    public required long Deterministic { get; init; }

    /// <summary>Attributed installs matched probabilistically.</summary>
    public required long Probabilistic { get; init; }

    /// <summary>Installs that could not be attributed at all.</summary>
    public required long Unmatched { get; init; }

    /// <summary>Mean confidence across the probabilistic matches only.</summary>
    public required decimal AverageProbabilisticConfidence { get; init; }

    /// <summary>Per strategy detail.</summary>
    public IReadOnlyList<MatchTypeSummary> ByMatchType { get; init; } = [];
}

/// <summary>Body of <c>POST /api/v1/webhooks</c> (FR-204).</summary>
public sealed record CreateWebhookRequest
{
    /// <summary>Destination URL. Validated against the same target policy as a link, so a webhook
    /// cannot be pointed at a cloud metadata endpoint (T-02).</summary>
    public required string Url { get; init; }

    /// <summary>Event types to deliver, for example <c>attribution.created</c>.</summary>
    public required IReadOnlyList<string> EventTypes { get; init; }

    /// <summary>
    /// Whether the subscription is active on creation. Omitted means active; only an explicit
    /// <see langword="false"/> registers a subscription that receives nothing until it is enabled.
    /// </summary>
    public bool? IsActive { get; init; }
}

/// <summary>Representation of a webhook subscription. The signing secret is never returned.</summary>
public sealed record WebhookResponse
{
    /// <summary>Subscription identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Destination URL.</summary>
    public required string Url { get; init; }

    /// <summary>Subscribed event types.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    /// <summary>Whether the subscription is active.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Envelope delivered to a webhook endpoint. Signed twice: a symmetric HMAC for simplicity and an
/// Ed25519 signature a third party can verify against the JWKS endpoint without holding the shared
/// secret. The second slot is also where a post-quantum signature lands later (§B.7.4, §E.5.3).
/// </summary>
public sealed record WebhookPayload
{
    /// <summary>Event type, for example <c>attribution.created</c>.</summary>
    public required string Event { get; init; }

    /// <summary>Unique delivery identifier, used by the receiver for replay protection.</summary>
    public required string Id { get; init; }

    /// <summary>When the underlying event happened.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Event payload.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>Body of <c>POST /api/v1/abuse-reports</c>. Public and rate limited (FR-245).</summary>
public sealed record CreateAbuseReportRequest
{
    /// <summary>The reported short URL.</summary>
    public required string Url { get; init; }

    /// <summary>Reason: <c>phishing</c>, <c>malware</c>, <c>spam</c>, <c>illegal</c>,
    /// <c>copyright</c> or <c>other</c>.</summary>
    public required string Reason { get; init; }

    /// <summary>Free text detail from the reporter.</summary>
    public string? Details { get; init; }

    /// <summary>Reporter contact address. Stored only as a hash, so follow up is possible without
    /// keeping an identifiable address (§E.6.3).</summary>
    public string? ReporterEmail { get; init; }
}

/// <summary>Acknowledgement of an abuse report. Deliberately does not confirm the link exists.</summary>
public sealed record AbuseReportResponse
{
    /// <summary>Report identifier the reporter can quote when following up.</summary>
    public required Guid Id { get; init; }

    /// <summary>Report status: <c>new</c>, <c>triaged</c>, <c>confirmed</c>, <c>rejected</c> or
    /// <c>resolved</c>.</summary>
    public required string Status { get; init; }

    /// <summary>When the report was received.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
