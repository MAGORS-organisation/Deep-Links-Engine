namespace Dle.Domain.Entities;

/// <summary>
/// A short link with its routing rules and metadata (FR-101). Maps to <c>links</c>.
/// </summary>
/// <remarks>
/// <see cref="QuarantinedAt"/> exists instead of a delete flag because an abusive link must be
/// answered with HTTP 410 and an explanation rather than silently removed: quiet deletion destroys
/// the forensic trail that a later complaint has to be answered from (§E.3, TC-103).
/// </remarks>
public class Link
{
    /// <summary>Primary key. A Snowflake identifier, not a sequence, so that it is time ordered
    /// and can be minted without a round trip to the database.</summary>
    public long Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Host that serves this link.</summary>
    public Guid DomainId { get; set; }

    /// <summary>Case insensitive slug, unique within the domain.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human readable title shown in the control plane.</summary>
    public string? Title { get; set; }

    /// <summary>Internal description of the link.</summary>
    public string? Description { get; set; }

    /// <summary>Web fallback target, used when no application can handle the link.</summary>
    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>Path handed to the application, for example <c>/product/123</c>.</summary>
    public string? DeeplinkPath { get; set; }

    /// <summary>Routing rules, stored as a JSON array (§B.5.4). They are data, not code: the
    /// first matching rule wins and a default rule is mandatory (FR-127, TC-105).</summary>
    public string RoutingRules { get; set; } = "[]";

    /// <summary>Open Graph metadata, stored as a JSON document.</summary>
    public string OgMeta { get; set; } = "{}";

    /// <summary>UTM parameters added to the target, stored as a JSON document.</summary>
    public string Utm { get; set; } = "{}";

    /// <summary>Campaign the link belongs to.</summary>
    public Guid? CampaignId { get; set; }

    /// <summary>Free form tags used for search and filtering (FR-109).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Whether the link is active. An inactive link answers 404.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Instant from which the link serves, in UTC (FR-104).</summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>Instant at which the link stops serving, in UTC.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Where an expired link sends the visitor instead (TC-104).</summary>
    public string? ExpiredUrl { get; set; }

    /// <summary>When the link was quarantined for abuse, in UTC. A quarantined link answers 410
    /// and is never redirected, whatever its other state says.</summary>
    public DateTimeOffset? QuarantinedAt { get; set; }

    /// <summary>Operator who created the link.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Instant of the last change, in UTC.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Monotonic version, incremented on every change and matching the highest
    /// <see cref="LinkVersion.Version"/> in the history (FR-107).</summary>
    public int Version { get; set; } = 1;
}
