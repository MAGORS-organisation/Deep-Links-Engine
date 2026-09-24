using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Analytics.Postgres;

/// <summary>
/// <see cref="IRetentionService"/> for a provider that expires raw events itself.
/// </summary>
/// <remarks>
/// Registered when <c>Dle:Analytics:Provider</c> selects ClickHouse, where the raw event tables
/// carry a <c>TTL</c> clause generated from the same <c>Dle:Privacy:Retention:RawDays</c> value
/// this service reports. Expiry is therefore the storage engine's own background merge, not a job
/// the engine schedules — but FR-247 still wants the policy to be visible and the run to be
/// recorded, so a pass reports what the policy is and where it is enforced instead of pretending
/// nothing happens.
/// </remarks>
public sealed partial class ProviderManagedRetentionService : IRetentionService
{
    private readonly IOptionsMonitor<AnalyticsRetentionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderManagedRetentionService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="options">Monitor over the retention options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ProviderManagedRetentionService(
        IOptionsMonitor<AnalyticsRetentionOptions> options,
        TimeProvider timeProvider,
        ILogger<ProviderManagedRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<RetentionRunResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        AnalyticsRetentionOptions options = _options.CurrentValue;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        LogDelegated(options.RawDays, options.AggregatedDays);

        return Task.FromResult(new RetentionRunResult
        {
            Id = Guid.CreateVersion7(now),
            StartedAt = now,
            FinishedAt = now,
            RawDays = options.RawDays,
            AggregatedDays = options.AggregatedDays,
            IpPrefixDays = options.IpPrefixDays,
            DryRun = true,
            PartitionsDropped = [],
            Status = RetentionRunResult.StatusDryRun,
        });
    }

    [LoggerMessage(
        EventId = 6410,
        Level = LogLevel.Information,
        Message = "Retention delegated to the storage engine: raw events expire after {RawDays} "
                  + "days through the ClickHouse table TTL, aggregates after {AggregatedDays} days.")]
    private partial void LogDelegated(int rawDays, int aggregatedDays);
}
