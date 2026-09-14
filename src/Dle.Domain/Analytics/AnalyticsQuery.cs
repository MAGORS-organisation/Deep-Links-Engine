namespace Dle.Domain.Analytics;

/// <summary>
/// Parameters of a reporting query (FR-202). The analytics store implementation translates
/// this into SQL for PostgreSQL or ClickHouse (ADR-006); the shape is deliberately provider
/// neutral and always tenant scoped.
/// </summary>
public sealed record AnalyticsQuery
{
    /// <summary>Tenant whose data is queried. Never optional — every report is tenant scoped.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Inclusive lower bound of the reported interval, in UTC.</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>Exclusive upper bound of the reported interval, in UTC.</summary>
    public required DateTimeOffset To { get; init; }

    /// <summary>Bucket size for time series results.</summary>
    public TimeGrain Grain { get; init; } = TimeGrain.Day;

    /// <summary>Restrict the report to a single link.</summary>
    public long? LinkId { get; init; }

    /// <summary>Restrict the report to a single campaign.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Restrict the report to one ISO-3166-1 alpha-2 country code.</summary>
    public string? Country { get; init; }

    /// <summary>Restrict the report to one platform name, for example <c>ios</c>.</summary>
    public string? Platform { get; init; }

    /// <summary>Whether crawler traffic is included. Defaults to <see langword="false"/> (FR-205).</summary>
    public bool IncludeBots { get; init; }

    /// <summary>Maximum number of rows returned for breakdown queries.</summary>
    public int Limit { get; init; } = 500;
}
