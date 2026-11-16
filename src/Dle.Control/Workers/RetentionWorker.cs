using Dle.Analytics.Postgres;
using Dle.Persistence;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Enforces the click stream retention policy.
/// </summary>
/// <remarks>
/// <para>
/// Retention has three parts and this pass does all three: hash the address after the configured
/// window, drop the raw data, and — the part that is easy to skip — make the job's own execution
/// auditable. A retention control nobody can prove ran is not a control. Each pass therefore leaves
/// three traces that have to agree: a row in <c>analytics_retention_runs</c> written by the service
/// itself, a structured log entry here naming what was removed, and the worker metric. A supervisory
/// authority asking "show me that your thirty day retention is real" is answered from the first;
/// an operator asking "did it run last night" is answered from the other two.
/// </para>
/// <para>
/// Raw events leave by partition rather than by <c>DELETE</c>, which matters more as a privacy
/// control than as a performance one: a delete leaves the rows readable in the heap until autovacuum
/// catches up, whereas detaching and dropping a partition is a catalogue operation that actually
/// removes the files.
/// </para>
/// <para>
/// The <c>dry_run</c> mode reports what it would remove and removes nothing. It exists for the first
/// pass after a policy change, which is the one pass where being wrong is expensive and
/// irreversible.
/// </para>
/// </remarks>
public sealed partial class RetentionWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.retention";

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkerOptions> _options;
    private readonly ILogger<RetentionWorker> _logger;

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
    public RetentionWorker(
        IServiceScopeFactory scopes,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<RetentionWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled => _options.CurrentValue.Enabled && _options.CurrentValue.Retention.Enabled;

    /// <inheritdoc />
    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(_options.CurrentValue.Retention.IntervalMinutes, 1));

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.Retention.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();

        // Two tables outside the click stream that only ever grow otherwise. Both carry their own
        // expiry, so there is no policy to configure: a claim code is written on every interstitial
        // an anonymous visitor loads, an idempotency record on every keyed write.
        int claimCodes = await PruneClaimCodesAsync(
            scope.ServiceProvider.GetRequiredService<DleDbContext>(),
            cancellationToken);
        int loginHints = await PruneLoginHintsAsync(
            scope.ServiceProvider.GetRequiredService<DleDbContext>(),
            cancellationToken);
        int idempotencyRecords = await scope.ServiceProvider
            .GetRequiredService<IdempotencyStore>()
            .PruneExpiredAsync(cancellationToken);

        Metrics.Items(Job, "claim_code_deleted", claimCodes);
        Metrics.Items(Job, "login_hint_deleted", loginHints);
        Metrics.Items(Job, "idempotency_record_deleted", idempotencyRecords);

        IRetentionService retention = scope.ServiceProvider.GetRequiredService<IRetentionService>();
        RetentionRunResult result = await retention.RunAsync(cancellationToken);

        Metrics.Items(Job, "partition", result.PartitionsDropped.Count);
        Metrics.Items(Job, "anonymised_row", result.RowsAnonymised);
        Metrics.Items(Job, "rollup_row_deleted", result.RollupRowsDeleted);

        // The audit of the run itself. It is logged as well as stored because the two
        // answer different questions: the row proves the policy was applied, the log line is what an
        // operator sees when they ask whether last night's pass did anything unusual.
        LogRetentionRun(
            _logger,
            result.Id,
            result.Status,
            result.RawDays,
            result.AggregatedDays,
            result.PartitionsDropped.Count,
            result.RowsAnonymised,
            result.RollupRowsDeleted,
            result.DryRun);

        if (string.Equals(result.Status, RetentionRunResult.StatusFailed, StringComparison.Ordinal))
        {
            // Surfaced as a failed pass rather than swallowed as a completed one: a retention job
            // that half ran is the case an operator most needs to see.
            throw new InvalidOperationException(
                "The retention pass failed: " + (result.Error ?? "no detail was recorded."));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{result.PartitionsDropped.Count} partitions removed, {result.RowsAnonymised} rows anonymised, "
            + $"{result.RollupRowsDeleted} rollup rows deleted, {claimCodes} claim codes, {loginHints} login hints and "
            + $"{idempotencyRecords} idempotency records pruned (status {result.Status}).");
    }

    /// <summary>Deletes the claim codes of every tenant that expired more than a day ago.</summary>
    /// <remarks>
    /// The day of grace keeps the honest answer for somebody who types an old code: "expired", not
    /// "unknown". After that the row is only a click identifier nobody can redeem.
    /// </remarks>
    private async Task<int> PruneClaimCodesAsync(DleDbContext db, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = db.BeginCrossTenantScope(
            "the retention pass removes the expired claim codes of every tenant");

        DateTimeOffset cutoff = Clock.GetUtcNow().AddDays(-1);

        return await db.ClaimCodes
            .AcrossTenants()
            .Where(c => c.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> PruneLoginHintsAsync(DleDbContext db, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = db.BeginCrossTenantScope(
            "Retention worker prunes expired S2 login hints across all tenants.");
        DateTimeOffset cutoff = Clock.GetUtcNow().AddDays(-1);

        return await db.ClickLoginHints
            .AcrossTenants()
            .Where(h => h.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 5630,
        Level = LogLevel.Information,
        Message = "Retention run {RunId} finished with status {Status}: raw {RawDays} d, aggregated "
            + "{AggregatedDays} d, {Partitions} partitions dropped, {AnonymisedRows} rows anonymised, "
            + "{RollupRows} rollup rows deleted, dry run {DryRun}.")]
    private static partial void LogRetentionRun(
        ILogger logger,
        Guid runId,
        string status,
        int rawDays,
        int aggregatedDays,
        int partitions,
        long anonymisedRows,
        long rollupRows,
        bool dryRun);
}
