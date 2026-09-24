using System.Diagnostics.Metrics;

namespace Dle.Control.Workers;

/// <summary>
/// The instruments the background workers publish (§C.6).
/// </summary>
/// <remarks>
/// <para>
/// A background job that fails silently is worse than one that does not exist, because the
/// deployment believes it is protected. Every pass of every worker therefore reports an outcome,
/// and <c>dle_domain_verification_failures</c> — the gauge §C.6 names and asks to be alerted on —
/// is published from here rather than from the worker, so that a worker which crashes on its first
/// line still leaves the last known value visible instead of leaving a blank panel.
/// </para>
/// <para>
/// Names use the OpenTelemetry dotted spelling; a Prometheus exporter renders
/// <c>dle.domain.verification.failures</c> as <c>dle_domain_verification_failures</c>.
/// </para>
/// </remarks>
public sealed class WorkerMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Control.Workers";

    /// <summary>Outcome of a pass that did its work.</summary>
    public const string OutcomeCompleted = "completed";

    /// <summary>Outcome of a pass another replica was already running.</summary>
    public const string OutcomeNotLeader = "not_leader";

    /// <summary>Outcome of a pass that threw.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>Outcome of a pass that was cut short by shutdown.</summary>
    public const string OutcomeCancelled = "cancelled";

    private readonly Meter _meter;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _items;
    private long _domainVerificationFailures;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host. When it is <see langword="null"/> the meter is created
    /// directly, so metrics still flow in a host that never called <c>AddMetrics</c>.
    /// </param>
    public WorkerMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _runs = _meter.CreateCounter<long>(
            "dle.worker.runs",
            unit: "{run}",
            description: "Worker passes, tagged by job and outcome. 'not_leader' is the normal case "
                + "on every replica but one (§B.3).");

        _duration = _meter.CreateHistogram<double>(
            "dle.worker.duration",
            unit: "s",
            description: "Wall clock time of one worker pass, tagged by job.");

        _items = _meter.CreateCounter<long>(
            "dle.worker.items",
            unit: "{item}",
            description: "Units of work a pass processed: domains verified, links re-checked, "
                + "deliveries attempted, partitions dropped.");

        _ = _meter.CreateObservableGauge(
            "dle.domain.verification.failures",
            () => Volatile.Read(ref _domainVerificationFailures),
            unit: "{domain}",
            description: "Domains whose association files or DNS are currently failing (§C.6). Alert above zero.");
    }

    /// <summary>Records the outcome of one pass.</summary>
    /// <param name="jobName">The job.</param>
    /// <param name="outcome">One of the outcome constants on this class.</param>
    /// <param name="elapsed">How long the pass took.</param>
    public void Run(string jobName, string outcome, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        KeyValuePair<string, object?> job = new("job", jobName);

        _runs.Add(1, job, new KeyValuePair<string, object?>("outcome", outcome));

        if (!string.Equals(outcome, OutcomeNotLeader, StringComparison.Ordinal))
        {
            _duration.Record(elapsed.TotalSeconds, job);
        }
    }

    /// <summary>Counts units of work a pass processed.</summary>
    /// <param name="jobName">The job.</param>
    /// <param name="kind">What was processed, for example <c>domain</c> or <c>delivery</c>.</param>
    /// <param name="count">How many.</param>
    public void Items(string jobName, string kind, long count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (count <= 0)
        {
            return;
        }

        _items.Add(
            count,
            new KeyValuePair<string, object?>("job", jobName),
            new KeyValuePair<string, object?>("kind", kind));
    }

    /// <summary>Publishes how many domains are currently failing verification (§C.6, FR-143).</summary>
    /// <param name="failures">The count from the last completed verification pass.</param>
    public void ReportDomainVerificationFailures(long failures) =>
        Volatile.Write(ref _domainVerificationFailures, failures);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
