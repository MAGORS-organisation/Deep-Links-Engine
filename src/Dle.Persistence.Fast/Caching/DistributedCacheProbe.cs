using Dle.Persistence.Fast.Telemetry;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Keeps <c>dle.cache.l2.up</c> honest: reads one key from the shared cache on a fixed cadence and
/// records whether the read came back (§D.6, the <c>cache_l2_down</c> signal).
/// </summary>
/// <remarks>
/// <para>
/// HybridCache swallows a failing L2 by design — a resolve must never wait on Valkey — which means
/// nothing on the request path can say "the shared cache is gone"; the operator would see a latency
/// change and a lower L2 hit ratio and have nothing that names the cause. Only a probe that asks the
/// same client the same question on a clock can.
/// </para>
/// <para>
/// The probe key is never written. A miss is a healthy answer — the cache spoke — and an exception
/// or a timeout is not. Transitions are logged once each way; the gauge carries the state.
/// </para>
/// </remarks>
public sealed partial class DistributedCacheProbe : BackgroundService
{
    /// <summary>The key read on every probe. Nothing writes it, so every answer is a miss.</summary>
    internal const string ProbeKey = "probe:l2";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly IDistributedCache _cache;
    private readonly FastPersistenceMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistributedCacheProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="cache">The L2 implementation HybridCache uses.</param>
    /// <param name="metrics">Where the state is published.</param>
    /// <param name="timeProvider">Clock for the cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DistributedCacheProbe(
        IDistributedCache cache,
        FastPersistenceMetrics metrics,
        TimeProvider timeProvider,
        ILogger<DistributedCacheProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? previous = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool reachable;

            try
            {
                reachable = await ProbeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _metrics.DistributedCacheProbed(reachable);

            if (previous != reachable)
            {
                if (reachable)
                {
                    LogDistributedCacheUp(_logger);
                }
                else
                {
                    LogDistributedCacheDown(_logger);
                }

                previous = reachable;
            }

            try
            {
                await Task.Delay(Interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(Budget);

        try
        {
            // The token reaches only part of the client: a connect attempt in progress ignores it
            // and can run for the client's own connect timeout. WaitAsync makes the budget the
            // probe's own deadline regardless; the attempt finishes on its own in the background.
            _ = await _cache.GetAsync(ProbeKey, budget.Token).WaitAsync(budget.Token);
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            LogProbeTimedOut(_logger, Budget.TotalSeconds);
            return false;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // Every failure the client can raise — connection refused, connection reset, protocol
        // error, a serializer — means the same thing here: the shared cache did not answer. The
        // exception is kept for the debug log; the gauge is the signal.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogProbeFailed(_logger, exception);
            return false;
        }
    }

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Warning,
        Message = "The shared (L2) cache stopped answering; resolves fall through to L1 and PostgreSQL with higher latency (§D.6).")]
    private static partial void LogDistributedCacheDown(ILogger logger);

    [LoggerMessage(
        EventId = 4302,
        Level = LogLevel.Information,
        Message = "The shared (L2) cache is answering again.")]
    private static partial void LogDistributedCacheUp(ILogger logger);

    [LoggerMessage(
        EventId = 4303,
        Level = LogLevel.Debug,
        Message = "The shared (L2) cache probe did not answer within {BudgetSeconds} s.")]
    private static partial void LogProbeTimedOut(ILogger logger, double budgetSeconds);

    [LoggerMessage(
        EventId = 4304,
        Level = LogLevel.Debug,
        Message = "The shared (L2) cache probe failed.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);
}
