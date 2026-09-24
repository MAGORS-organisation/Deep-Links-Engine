using Dle.Analytics.Postgres;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Aggregates the click stream into the reporting tables (§B.3 component C-09, FR-202).
/// </summary>
/// <remarks>
/// <para>
/// The schedule lives here rather than inside the analytics module, and that is a deliberate
/// separation: <c>IRollupService</c> knows how to aggregate and knows nothing about when, which is
/// what lets the ClickHouse provider satisfy the same port with an implementation that reports
/// there is nothing to schedule. One place owns "when jobs run" for the whole engine.
/// </para>
/// <para>
/// A pass is idempotent by construction — whole buckets are recomputed rather than incremented, and
/// each pass re-scans a few hours before the previous watermark so a late arrival is still counted.
/// That is what makes it safe to run this every few minutes, safe to run it twice, and safe to let
/// another replica take over after a crash.
/// </para>
/// </remarks>
public sealed class RollupWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.rollup";

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkerOptions> _options;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="leader">Leader election.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public RollupWorker(
        IServiceScopeFactory scopes,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<RollupWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);

        _scopes = scopes;
        _options = options;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled => _options.CurrentValue.Enabled && _options.CurrentValue.Rollup.Enabled;

    /// <inheritdoc />
    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(_options.CurrentValue.Rollup.IntervalMinutes, 1));

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.Rollup.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        IRollupService rollups = scope.ServiceProvider.GetRequiredService<IRollupService>();
        RollupRunResult result = await rollups.RunAsync(cancellationToken);

        long rows = result.ClickRowsHourly
            + result.InstallRowsHourly
            + result.ClickRowsDaily
            + result.InstallRowsDaily
            + result.AttributionQualityRows;

        Metrics.Items(Job, "rollup_row", rows);

        return result.DidWork
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{rows} rollup rows written; hourly watermark now {result.HourlyThrough:O}, daily {result.DailyThrough:O}.")
            : "nothing new to aggregate.";
    }
}
