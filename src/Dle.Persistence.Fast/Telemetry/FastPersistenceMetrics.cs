using System.Diagnostics.Metrics;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// The instruments this module publishes (§C.6).
/// </summary>
/// <remarks>
/// <para>
/// Instrument names use the OpenTelemetry dotted spelling. A Prometheus exporter renders
/// <c>dle.click_events.dropped</c> as <c>dle_click_events_dropped_total</c>, which is the metric §C.6
/// names and puts an alert on. That alert is the point: dropping click events is a deliberate trade
/// under load (NFR-06), but a silent one would mean campaign numbers quietly going wrong with nothing
/// to notice it.
/// </para>
/// <para>
/// <see cref="MeterName"/> is public so the telemetry module can add this meter to the OpenTelemetry
/// pipeline without a string literal duplicated across projects.
/// </para>
/// </remarks>
public sealed class FastPersistenceMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Persistence.Fast";

    private readonly Meter _meter;
    private readonly Counter<long> _clickEventsDropped;
    private readonly Counter<long> _clickEventsWritten;
    private readonly Counter<long> _sdkEventsWritten;
    private readonly Counter<long> _batchWriteFailures;

    /// <summary>
    /// Last probe result for the shared cache: <c>-1</c> until the first probe (or forever, when no
    /// L2 is configured), then <c>1</c> for answering and <c>0</c> for unreachable.
    /// </summary>
    private long _distributedCacheUp = -1;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host, so a test can observe the instruments through
    /// <c>MetricCollector</c>. When it is <see langword="null"/> — a host that never called
    /// <c>AddMetrics</c> — the meter is created directly, so metrics still flow to any listener.
    /// </param>
    public FastPersistenceMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _clickEventsDropped = _meter.CreateCounter<long>(
            "dle.click_events.dropped",
            unit: "{event}",
            description: "Click events discarded because the bounded channel was full. Any non-zero rate is an alert (§C.6).");

        _clickEventsWritten = _meter.CreateCounter<long>(
            "dle.click_events.written",
            unit: "{event}",
            description: "Click events successfully copied into the click stream.");

        _sdkEventsWritten = _meter.CreateCounter<long>(
            "dle.sdk_events.written",
            unit: "{event}",
            description: "SDK reported events successfully copied into storage.");

        _batchWriteFailures = _meter.CreateCounter<long>(
            "dle.event_batch.failures",
            unit: "{batch}",
            description: "Batches that could not be written and were discarded.");

        _ = _meter.CreateObservableGauge(
            "dle.cache.l2.up",
            ObserveDistributedCacheUp,
            unit: "1",
            description: "1 while the shared (L2) cache answers, 0 while it is unreachable and resolves fall through to L1 and PostgreSQL (§D.6, cache_l2_down). Not reported when no L2 is configured.");
    }

    /// <summary>Counts one click event dropped by the bounded channel.</summary>
    public void ClickEventDropped() => _clickEventsDropped.Add(1);

    /// <summary>Counts a batch of click events written to storage.</summary>
    /// <param name="count">Number of rows in the batch.</param>
    public void ClickEventsWritten(int count) => _clickEventsWritten.Add(count);

    /// <summary>Counts a batch of SDK events written to storage.</summary>
    /// <param name="count">Number of rows in the batch.</param>
    public void SdkEventsWritten(int count) => _sdkEventsWritten.Add(count);

    /// <summary>Records the latest answer of the shared cache probe.</summary>
    /// <param name="reachable">Whether the shared cache answered the probe.</param>
    public void DistributedCacheProbed(bool reachable) =>
        Interlocked.Exchange(ref _distributedCacheUp, reachable ? 1 : 0);

    /// <summary>Counts one batch that failed to write.</summary>
    /// <param name="kind">Which stream failed: <c>click</c> or <c>sdk</c>.</param>
    public void BatchWriteFailed(string kind) => _batchWriteFailures.Add(1, new KeyValuePair<string, object?>("stream", kind));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveDistributedCacheUp()
    {
        long state = Interlocked.Read(ref _distributedCacheUp);

        if (state >= 0)
        {
            yield return new Measurement<long>(state);
        }
    }
}
