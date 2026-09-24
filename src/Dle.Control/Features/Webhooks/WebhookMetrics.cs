using System.Diagnostics.Metrics;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// The instruments the webhook module publishes (§C.6).
/// </summary>
/// <remarks>
/// <para>
/// §C.6 names two: <c>dle_webhook_delivery_duration_seconds</c> as a histogram and
/// <c>dle_webhook_dlq_size</c> as a gauge. The gauge is the one that matters operationally. A
/// delivery that exhausts its retries is a promise the product failed to keep, and unless the
/// number of them is on a dashboard the failure is invisible: the customer's data is simply
/// missing, and nobody finds out until a reconciliation months later.
/// </para>
/// <para>
/// Instrument names use the OpenTelemetry dotted spelling; a Prometheus exporter renders
/// <c>dle.webhook.delivery.duration</c> as <c>dle_webhook_delivery_duration_seconds</c> and
/// <c>dle.webhook.dlq.size</c> as <c>dle_webhook_dlq_size</c>.
/// </para>
/// </remarks>
public sealed class WebhookMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Control.Webhooks";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _deliveries;
    private readonly Counter<long> _deadLettered;
    private long _dlqSize;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host, so a test can observe the instruments through
    /// <c>MetricCollector</c>. When it is <see langword="null"/> the meter is created directly.
    /// </param>
    public WebhookMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _duration = _meter.CreateHistogram<double>(
            "dle.webhook.delivery.duration",
            unit: "s",
            description: "Wall clock time of one webhook delivery attempt, tagged by outcome (§C.6).");

        _deliveries = _meter.CreateCounter<long>(
            "dle.webhook.delivery",
            unit: "{delivery}",
            description: "Webhook delivery attempts, tagged by outcome and event type.");

        _deadLettered = _meter.CreateCounter<long>(
            "dle.webhook.dead_lettered",
            unit: "{delivery}",
            description: "Deliveries that exhausted their retries. Every one of these is data the "
                + "customer never received (FR-204).");

        _ = _meter.CreateObservableGauge(
            "dle.webhook.dlq.size",
            () => Volatile.Read(ref _dlqSize),
            unit: "{delivery}",
            description: "Deliveries currently sitting in the dead letter queue (§C.6). Alert above zero.");
    }

    /// <summary>Records one delivery attempt.</summary>
    /// <param name="eventType">The event type that was delivered.</param>
    /// <param name="outcome">One of <c>delivered</c>, <c>failed</c>, <c>dead</c> or
    /// <c>unsendable</c>.</param>
    /// <param name="elapsed">How long the attempt took.</param>
    public void Attempt(string eventType, string outcome, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        KeyValuePair<string, object?> outcomeTag = new("outcome", outcome);
        KeyValuePair<string, object?> eventTag = new("event_type", eventType);

        _duration.Record(elapsed.TotalSeconds, outcomeTag, eventTag);
        _deliveries.Add(1, outcomeTag, eventTag);
    }

    /// <summary>Counts one delivery that exhausted its retries.</summary>
    /// <param name="eventType">The event type that was lost.</param>
    public void DeadLettered(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        _deadLettered.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    }

    /// <summary>Publishes the current depth of the dead letter queue.</summary>
    /// <param name="size">The number of dead deliveries counted by the last dispatcher pass.</param>
    /// <remarks>
    /// Reported by the dispatcher rather than computed by the gauge callback: the callback runs on
    /// the exporter's schedule and must not open a database connection, whereas the dispatcher is
    /// already looking at the table.
    /// </remarks>
    public void ReportDeadLetterQueueSize(long size) => Volatile.Write(ref _dlqSize, size);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
