using System.Diagnostics;
using System.Security.Cryptography;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.Control.Workers;

/// <summary>
/// The shape every background worker in the control plane takes: leader elected, cancellation
/// aware, failure isolated, instrumented (§B.3, §C.6).
/// </summary>
/// <remarks>
/// <para>
/// Four properties, and every one of them is here rather than in each worker because every one of
/// them is the sort of thing that gets forgotten exactly once and then costs a weekend.
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Exactly once across replicas.</b> Each pass takes a PostgreSQL advisory lock; the
///     replicas that lose simply sleep. Three replicas running a nightly retention job three times
///     is survivable, three replicas each delivering the same webhook is not.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>A crash cannot take the host down.</b> An unhandled exception in a
///     <see cref="BackgroundService"/> stops the whole application by default in modern .NET
///     hosting. A URLhaus outage or a customer's broken TLS certificate must not stop the control
///     plane API, so every pass is wrapped, the failure is logged with its job name and counted,
///     and the loop continues.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Cancellation is honoured.</b> The token flows into the pass, and a cancellation during
///     shutdown is recorded as such rather than as a failure — otherwise every deployment
///     manufactures a burst of false alarms.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Jittered start.</b> Replicas start within seconds of each other during a rolling deploy,
///     so without jitter they contend for every lock at the same instant and the losers all wake
///     again at the same instant.
///     </description>
///   </item>
/// </list>
/// <para>
/// SHARED-KERNEL §17.9 applies to the catch below: it is not empty, it names the job, and its
/// default is to skip the pass rather than to continue in an unknown state.
/// </para>
/// </remarks>
public abstract partial class LeaderElectedBackgroundService : BackgroundService
{
    private readonly PostgresLeaderLock _leader;
    private readonly WorkerMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="leader">Leader election over the advisory lock.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="timeProvider">Clock, also used for the timer so a test can advance it.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    protected LeaderElectedBackgroundService(
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _leader = leader;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Stable name of this job, used for the lock key, the logs and the metric tag.</summary>
    protected abstract string JobName { get; }

    /// <summary>Whether this worker should run at all.</summary>
    protected abstract bool IsEnabled { get; }

    /// <summary>Interval between passes.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Whether a pass runs immediately rather than after the first interval.</summary>
    protected abstract bool RunAtStartup { get; }

    /// <summary>Upper bound on taking the leader lock.</summary>
    protected abstract TimeSpan LockTimeout { get; }

    /// <summary>Random delay added before the first pass, to spread replicas out.</summary>
    protected abstract TimeSpan StartupJitter { get; }

    /// <summary>Instruments, so a worker can count what it processed.</summary>
    protected WorkerMetrics Metrics => _metrics;

    /// <summary>Clock (SHARED-KERNEL §17.2).</summary>
    protected TimeProvider Clock => _timeProvider;

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            LogDisabled(_logger, JobName);
            return;
        }

        LogStarted(_logger, JobName, Interval);

        await DelayJitterAsync(stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (RunAtStartup)
        {
            await RunOnceAsync(stoppingToken);
        }

        using PeriodicTimer timer = new(Interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Does the work of one pass. Called only on the replica that holds the leader lock.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token, cancelled at shutdown.</param>
    /// <returns>A short description of what the pass did, for the log.</returns>
    protected abstract Task<string> RunPassAsync(CancellationToken cancellationToken);

    /// <summary>Takes the lock, runs one pass, and never lets a failure escape.</summary>
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        await using LeaderLease? lease = await _leader.TryAcquireAsync(JobName, LockTimeout, cancellationToken);

        if (lease is null)
        {
            // Another replica is running this pass, or the database is unreachable. Either way this
            // replica is not the leader and does nothing (§B.3).
            _metrics.Run(JobName, WorkerMetrics.OutcomeNotLeader, TimeSpan.Zero);
            return;
        }

        try
        {
            string summary = await RunPassAsync(cancellationToken);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            _metrics.Run(JobName, WorkerMetrics.OutcomeCompleted, elapsed);
            LogCompleted(_logger, JobName, elapsed.TotalSeconds, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a fault. Counted separately so a deploy does not look like an incident.
            _metrics.Run(JobName, WorkerMetrics.OutcomeCancelled, Stopwatch.GetElapsedTime(started));
            LogCancelled(_logger, JobName);
        }
#pragma warning disable CA1031 // A worker fault must not stop the host; see the class remarks.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Explicit default: the pass is abandoned, the failure is named and counted, and the
            // loop continues. Rethrowing would stop the BackgroundService and, with it, the whole
            // control plane (SHARED-KERNEL §17.9).
            _metrics.Run(JobName, WorkerMetrics.OutcomeFailed, Stopwatch.GetElapsedTime(started));
            LogFailed(_logger, JobName, exception);
        }
    }

    /// <summary>Waits a random fraction of the configured jitter before the first pass.</summary>
    private async Task DelayJitterAsync(CancellationToken cancellationToken)
    {
        TimeSpan jitter = StartupJitter;

        if (jitter <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan delay = TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32((int)jitter.TotalMilliseconds + 1));

        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during the initial delay. Nothing has run, so there is nothing to unwind.
        }
    }

    [LoggerMessage(
        EventId = 5610,
        Level = LogLevel.Information,
        Message = "Worker {JobName} is disabled by configuration and will not run.")]
    private static partial void LogDisabled(ILogger logger, string jobName);

    [LoggerMessage(
        EventId = 5611,
        Level = LogLevel.Information,
        Message = "Worker {JobName} started with an interval of {Interval}.")]
    private static partial void LogStarted(ILogger logger, string jobName, TimeSpan interval);

    [LoggerMessage(
        EventId = 5612,
        Level = LogLevel.Information,
        Message = "Worker {JobName} completed a pass in {Seconds:F1} s: {Summary}")]
    private static partial void LogCompleted(ILogger logger, string jobName, double seconds, string summary);

    [LoggerMessage(
        EventId = 5613,
        Level = LogLevel.Error,
        Message = "Worker {JobName} failed a pass. The worker keeps running and will try again at the next interval.")]
    private static partial void LogFailed(ILogger logger, string jobName, Exception exception);

    [LoggerMessage(
        EventId = 5614,
        Level = LogLevel.Information,
        Message = "Worker {JobName} was cancelled during shutdown.")]
    private static partial void LogCancelled(ILogger logger, string jobName);
}
