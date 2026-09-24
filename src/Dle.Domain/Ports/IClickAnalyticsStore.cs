using Dle.Domain.Analytics;

namespace Dle.Domain.Ports;

/// <summary>
/// Reporting side of the analytics storage (ADR-006): PostgreSQL partitions by default,
/// ClickHouse as an opt-in. Everything the reporting API needs is expressed here so that the
/// choice of engine stays an operator decision rather than an application rewrite.
/// </summary>
/// <remarks>
/// Every method is tenant scoped through <see cref="AnalyticsQuery.TenantId"/> and honours
/// <see cref="AnalyticsQuery.IncludeBots"/>, which defaults to excluding crawler traffic so that
/// a preview fetch never inflates a campaign (FR-205, TC-106).
/// </remarks>
public interface IClickAnalyticsStore
{
    /// <summary>Clicks, installs and conversions bucketed over time.</summary>
    /// <param name="query">The report parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Buckets in ascending order, including empty ones.</returns>
    Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(AnalyticsQuery query, CancellationToken ct);

    /// <summary>Totals grouped by one dimension.</summary>
    /// <param name="query">The report parameters.</param>
    /// <param name="dimension">Dimension to group by: <c>country</c>, <c>region</c>,
    /// <c>platform</c>, <c>os_family</c>, <c>device_class</c>, <c>channel</c>, <c>language</c>,
    /// <c>referrer_host</c>, <c>link</c>, <c>campaign</c> or <c>ab_variant</c>. Implementations
    /// must map the name to a column through a fixed allowlist and must never interpolate it into
    /// SQL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows ordered by clicks descending, truncated to <see cref="AnalyticsQuery.Limit"/>.</returns>
    Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(AnalyticsQuery query, string dimension, CancellationToken ct);

    /// <summary>The click to conversion funnel.</summary>
    /// <param name="query">The report parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The funnel summary, with attributed installs reported separately from installs.</returns>
    Task<FunnelSummary> GetFunnelAsync(AnalyticsQuery query, CancellationToken ct);

    /// <summary>Distribution of attribution strategies, for the honest dashboard of ADR-008.</summary>
    /// <param name="query">The report parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One row per match type that occurred, including <c>none</c>.</returns>
    Task<IReadOnlyList<MatchTypeSummary>> GetMatchTypesAsync(AnalyticsQuery query, CancellationToken ct);
}
