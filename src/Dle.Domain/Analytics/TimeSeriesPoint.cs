namespace Dle.Domain.Analytics;

/// <summary>
/// One bucket of a time series report (FR-202). Buckets are aligned in UTC according to
/// <see cref="AnalyticsQuery.Grain"/> and are returned in ascending order.
/// </summary>
/// <remarks>
/// Buckets with no traffic are still returned with zero counters, so a chart never has to
/// guess whether a gap means "no data" or "no traffic".
/// </remarks>
/// <param name="Bucket">Inclusive start of the bucket in UTC.</param>
/// <param name="Clicks">Number of human clicks in the bucket. Crawler traffic is excluded unless
/// <see cref="AnalyticsQuery.IncludeBots"/> is set (FR-205).</param>
/// <param name="Installs">Number of first application opens attributed to the bucket.</param>
/// <param name="Conversions">Number of conversion events reported by the SDK in the bucket.</param>
public sealed record TimeSeriesPoint(DateTimeOffset Bucket, long Clicks, long Installs, long Conversions);
