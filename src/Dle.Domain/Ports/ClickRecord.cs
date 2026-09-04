namespace Dle.Domain.Ports;

/// <summary>
/// The subset of a click that attribution needs to read back later (§B.6.2, §B.6.3).
/// </summary>
/// <remarks>
/// This is deliberately not the stored click event: it carries no IP hash and no user agent
/// detail, only what a match can legitimately be based on. Keeping the read model narrower than
/// the write model is what stops the attribution path from quietly becoming a re-identification
/// tool.
/// </remarks>
public sealed record ClickRecord
{
    /// <summary>Identifier of the click event row.</summary>
    public required Guid Id { get; init; }

    /// <summary>Instant of the click, in UTC. Drives both partition pruning and the time decay of
    /// a probabilistic score.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Tenant that owns the click.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Link that was clicked.</summary>
    public required long LinkId { get; init; }

    /// <summary>Public click identifier.</summary>
    public required string ClickId { get; init; }

    /// <summary>Truncated network prefix recorded with the click, under full consent only.</summary>
    public string? IpPrefix { get; init; }

    /// <summary>Operating system family recorded with the click.</summary>
    public string? OsFamily { get; init; }

    /// <summary>Operating system version recorded with the click.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Primary language subtag recorded with the click.</summary>
    public string? Language { get; init; }

    /// <summary>Country recorded with the click.</summary>
    public string? Country { get; init; }

    /// <summary>Deep link path the click resolved to, which is what the application opens.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Additional attributes carried with the click, such as the UTM set.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}
