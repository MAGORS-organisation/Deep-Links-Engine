using System.ComponentModel.DataAnnotations;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Configuration of the analytics module, bound from the <c>Dle:Analytics</c> section
/// (SHARED-KERNEL §16).
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>Name of the configuration section this type is bound from.</summary>
    public const string SectionName = "Dle:Analytics";

    /// <summary>
    /// Storage provider: <c>postgres</c> (default) or <c>clickhouse</c> (ADR-006).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = AnalyticsProviderNames.Postgres;

    /// <summary>Per-statement timeout for reporting queries, in seconds.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Upper bound on the number of rows a breakdown query may return, whatever
    /// <see cref="AnalyticsQuery.Limit"/> asks for. A report is a report, not an export; FR-203
    /// exports go through <c>IAnalyticsExporter</c>.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxBreakdownRows { get; set; } = 5_000;

    /// <summary>
    /// How far behind the current time the rollup watermark is allowed to advance, in minutes.
    /// The click stream is written from a bounded channel, so the most recent minutes are still
    /// arriving; rolling them up would freeze an incomplete bucket.
    /// </summary>
    [Range(0, 1440)]
    public int RollupLagMinutes { get; set; } = 10;

    /// <summary>
    /// How far back before the previous watermark each rollup pass re-scans, in hours. Buckets are
    /// recomputed rather than incremented, so re-scanning is idempotent and is what lets a late
    /// arrival still be counted.
    /// </summary>
    [Range(1, 168)]
    public int RollupOverlapHours { get; set; } = 3;

    /// <summary>
    /// Largest window a single rollup pass will process, in hours. Bounds the work done by the
    /// first pass after a long outage, so the job makes progress in several short transactions
    /// instead of one that never finishes.
    /// </summary>
    [Range(1, 8760)]
    public int RollupMaxWindowHours { get; set; } = 168;

    /// <summary>
    /// Whether reporting queries may be served from the rollup tables. Turning it off forces every
    /// report onto raw <c>click_events</c>, which is slower but answers "is the rollup lying?"
    /// without a deployment.
    /// </summary>
    public bool UseRollups { get; set; } = true;
}
