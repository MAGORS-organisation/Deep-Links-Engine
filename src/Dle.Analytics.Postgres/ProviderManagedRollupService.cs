using Microsoft.Extensions.Logging;

namespace Dle.Analytics.Postgres;

/// <summary>
/// <see cref="IRollupService"/> for a provider that needs no rollups.
/// </summary>
/// <remarks>
/// Registered when <c>Dle:Analytics:Provider</c> selects ClickHouse. ClickHouse aggregates raw
/// events on read fast enough that a rollup would only add a staleness window and a job to
/// operate, so there is genuinely nothing for a rollup pass to do — this is the correct behaviour
/// for that provider, not a placeholder. The service is still registered so the rollup worker can
/// depend on <see cref="IRollupService"/> unconditionally instead of branching on configuration it
/// should not have to know about.
/// </remarks>
public sealed partial class ProviderManagedRollupService : IRollupService
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderManagedRollupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ProviderManagedRollupService(
        TimeProvider timeProvider,
        ILogger<ProviderManagedRollupService> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<RollupRunResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        LogNotApplicable();

        return Task.FromResult(RollupRunResult.Idle(now));
    }

    [LoggerMessage(
        EventId = 6400,
        Level = LogLevel.Debug,
        Message = "Analytics rollup skipped: the selected provider aggregates raw events on read.")]
    private partial void LogNotApplicable();
}
