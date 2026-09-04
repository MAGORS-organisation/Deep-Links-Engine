namespace Dle.Analytics.Postgres;

/// <summary>
/// What one pass of <see cref="IRollupService"/> did.
/// </summary>
/// <remarks>
/// Returned rather than only logged so the worker that schedules the job can expose it as a
/// metric, and so a test can assert that a pass advanced the watermark without reading log text.
/// </remarks>
public sealed record RollupRunResult
{
    /// <summary>A pass that found nothing new to aggregate.</summary>
    /// <param name="at">The instant the pass ran.</param>
    /// <returns>An empty result whose watermarks are <paramref name="at"/>.</returns>
    public static RollupRunResult Idle(DateTimeOffset at) => new()
    {
        StartedAt = at,
        FinishedAt = at,
        HourlyFrom = at,
        HourlyThrough = at,
        DailyFrom = at,
        DailyThrough = at,
    };

    /// <summary>When the pass started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the pass finished.</summary>
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Inclusive lower bound of the hourly window that was recomputed.</summary>
    public required DateTimeOffset HourlyFrom { get; init; }

    /// <summary>Exclusive upper bound of the hourly window, and the new hourly watermark.</summary>
    public required DateTimeOffset HourlyThrough { get; init; }

    /// <summary>Inclusive lower bound of the daily window that was recomputed.</summary>
    public required DateTimeOffset DailyFrom { get; init; }

    /// <summary>Exclusive upper bound of the daily window, and the new daily watermark.</summary>
    public required DateTimeOffset DailyThrough { get; init; }

    /// <summary>Rows written or updated in the hourly click rollup.</summary>
    public int ClickRowsHourly { get; init; }

    /// <summary>Rows written or updated in the hourly install and conversion rollup.</summary>
    public int InstallRowsHourly { get; init; }

    /// <summary>Rows written or updated in the daily click rollup.</summary>
    public int ClickRowsDaily { get; init; }

    /// <summary>Rows written or updated in the daily install and conversion rollup.</summary>
    public int InstallRowsDaily { get; init; }

    /// <summary>Rows written or updated in the attribution quality rollup (ADR-008).</summary>
    public int AttributionQualityRows { get; init; }

    /// <summary>Whether the pass aggregated anything at all.</summary>
    public bool DidWork => HourlyThrough > HourlyFrom || DailyThrough > DailyFrom;
}
