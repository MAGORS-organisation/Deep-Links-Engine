using System.Threading.Channels;

using Dle.Persistence.Fast.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// Drains the bounded click event channel and writes it out in batches (§C.3.2).
/// </summary>
/// <remarks>
/// <para>
/// The batching rule is "up to <c>ClickEventBatchSize</c> rows or <c>ClickEventFlushMilliseconds</c>,
/// whichever comes first". Both halves matter: the row cap is what makes the throughput target of
/// §C.3.2 reachable, and the time cap is what keeps a quiet tenant's single click from sitting in
/// memory until the next busy period — and from being lost if the process is replaced first.
/// </para>
/// <para>
/// A batch that cannot be written is logged, counted and dropped. Retrying it in place would stall the
/// drain loop, the channel would fill behind it, and the sink would start dropping events at the point
/// where a redirect is being served — turning a database incident into a slow, self-inflicted loss of
/// the very telemetry that would explain it. Persisting a failed batch elsewhere is the control plane's
/// outbox, not this loop's job.
/// </para>
/// <para>
/// No wall clock is read anywhere: the flush deadline comes from the injected
/// <see cref="TimeProvider"/>, which is also what makes the timing testable (SHARED-KERNEL §17.2).
/// </para>
/// </remarks>
public sealed partial class ClickEventBatchWriter : BackgroundService
{
    private const string StreamName = "click";

    private readonly ChannelClickEventSink _sink;
    private readonly IClickEventWriter _writer;
    private readonly FastPersistenceMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClickEventBatchWriter> _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _writeTimeout;

    /// <summary>
    /// Creates the background writer.
    /// </summary>
    /// <param name="sink">The sink whose channel is drained.</param>
    /// <param name="writer">The batch writer, normally <see cref="CopyClickEventWriter"/>.</param>
    /// <param name="metrics">Metrics for written and failed batches.</param>
    /// <param name="options">Hot-path persistence options; supplies the batch size and the flush interval.</param>
    /// <param name="timeProvider">Clock used for the flush deadline and the write budget.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ClickEventBatchWriter(
        ChannelClickEventSink sink,
        IClickEventWriter writer,
        FastPersistenceMetrics metrics,
        FastPersistenceOptions options,
        TimeProvider timeProvider,
        ILogger<ClickEventBatchWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sink = sink;
        _writer = writer;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _batchSize = options.ClickEventBatchSize;
        _flushInterval = options.ClickEventFlushInterval;
        _writeTimeout = options.ShutdownFlushTimeout;
    }

    /// <summary>
    /// Closes the channel before the base implementation signals the drain loop, so that the loop sees
    /// a completed channel and writes what is left instead of waiting for events that will never come.
    /// </summary>
    /// <param name="cancellationToken">Token bounding the host's shutdown.</param>
    /// <returns>A task that completes when the loop has stopped.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = _sink.Complete();

        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<ClickEvent>(_batchSize);
        ChannelReader<ClickEvent> reader = _sink.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool channelOpen;

            try
            {
                channelOpen = await FillAsync(reader, buffer, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (buffer.Count > 0)
            {
                await FlushAsync(buffer);
                buffer.Clear();
            }

            if (!channelOpen)
            {
                return;
            }
        }

        await DrainRemainingAsync(reader, buffer);
    }

    /// <summary>
    /// Fills the buffer with up to one batch, returning as soon as the batch is full or the flush
    /// interval has elapsed.
    /// </summary>
    /// <returns><see langword="false"/> when the channel has completed and will produce nothing more.</returns>
    private async Task<bool> FillAsync(ChannelReader<ClickEvent> reader, List<ClickEvent> buffer, CancellationToken ct)
    {
        // Wait indefinitely for the first event: an idle instance must not spin, and there is nothing
        // to flush until something arrives.
        if (!await reader.WaitToReadAsync(ct))
        {
            return false;
        }

        // The deadline starts at the first event of the batch, so the oldest row in a batch is never
        // older than the flush interval.
        using CancellationTokenSource deadline = new(_flushInterval, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        while (buffer.Count < _batchSize)
        {
            if (reader.TryRead(out ClickEvent? item))
            {
                buffer.Add(item);
                continue;
            }

            try
            {
                if (!await reader.WaitToReadAsync(linked.Token))
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                // The flush interval elapsed, or the host is stopping. Either way, write what is held.
                break;
            }
        }

        return true;
    }

    /// <summary>Writes everything the channel still holds, in batch-sized chunks, during shutdown.</summary>
    private async Task DrainRemainingAsync(ChannelReader<ClickEvent> reader, List<ClickEvent> buffer)
    {
        buffer.Clear();

        while (reader.TryRead(out ClickEvent? item))
        {
            buffer.Add(item);

            if (buffer.Count >= _batchSize)
            {
                await FlushAsync(buffer);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(buffer);
            buffer.Clear();
        }
    }

    /// <summary>
    /// Writes one batch, bounded by its own timeout rather than by the host's shutdown token.
    /// </summary>
    /// <remarks>
    /// Deliberately not linked to the stopping token: a batch already handed to Postgres should finish
    /// rather than be abandoned mid-<c>COPY</c> because the host started shutting down a millisecond
    /// later. The independent timeout is what stops that from becoming an unbounded wait.
    /// </remarks>
    private async Task FlushAsync(List<ClickEvent> buffer)
    {
        using CancellationTokenSource timeout = new(_writeTimeout, _timeProvider);

        try
        {
            await _writer.WriteBatchAsync(buffer, timeout.Token);
            _metrics.ClickEventsWritten(buffer.Count);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // The drain loop is the last line of defence for the response path: whatever a driver, a
        // network stack or a serializer throws here, the loop has to survive it and keep draining.
        // The failure is never swallowed — it is logged with its exception and counted on
        // dle_event_batch_failures, which is what makes it visible (SHARED-KERNEL §17.9).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBatchFailed(_logger, buffer.Count, ex);
            _metrics.BatchWriteFailed(StreamName);
        }
    }

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Error,
        Message = "Discarded a batch of {RowCount} click events that could not be written.")]
    private static partial void LogBatchFailed(ILogger logger, int rowCount, Exception exception);
}
