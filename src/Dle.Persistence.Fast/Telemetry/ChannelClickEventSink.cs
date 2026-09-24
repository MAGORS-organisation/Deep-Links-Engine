using System.Threading.Channels;

using Dle.Persistence.Fast.Configuration;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// The non-blocking hand-off from the resolve path to the click stream writer (NFR-06, FR-165).
/// </summary>
/// <remarks>
/// <para>
/// The channel is bounded and uses <see cref="BoundedChannelFullMode.DropWrite"/>, which is the whole
/// design in one setting: when the writer cannot keep up, the event being offered is discarded and the
/// caller returns immediately. The alternative — waiting for room — would make a user's redirect
/// depend on the health of the analytics database, turning an analytics incident into a user-visible
/// outage. Losing rows is the cheaper failure, and FR-165 says so explicitly.
/// </para>
/// <para>
/// <see cref="BoundedChannelFullMode.DropWrite"/> drops the incoming event rather than an already
/// queued one, so the batch in flight stays intact and the loss is confined to the peak.
/// </para>
/// <para>
/// Drops are counted twice on purpose: <see cref="DroppedCount"/> is a process-lifetime total that
/// health endpoints and tests can read synchronously, and the metric behind
/// <c>dle_click_events_dropped_total</c> is what the alert of §C.6 fires on. Dropping is a signal, not
/// silence.
/// </para>
/// </remarks>
public sealed class ChannelClickEventSink : IClickEventSink
{
    // Channel<T>'s itemDropped callback runs synchronously on the thread inside TryWrite, so a thread
    // local flag is enough to tell "accepted" from "dropped" for this call. It has to be done this way:
    // with any Drop* full mode, ChannelWriter.TryWrite returns true even when it discarded the item, and
    // IClickEventSink.TryWrite is contractually false on a drop.
    [ThreadStatic]
    private static bool t_dropped;

    private readonly Channel<ClickEvent> _channel;
    private readonly FastPersistenceMetrics _metrics;
    private long _droppedCount;

    /// <summary>
    /// Creates the sink.
    /// </summary>
    /// <param name="options">Hot-path persistence options; supplies the channel capacity.</param>
    /// <param name="metrics">Metrics for the drop counter.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ChannelClickEventSink(FastPersistenceOptions options, FastPersistenceMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);

        _metrics = metrics;

        var channelOptions = new BoundedChannelOptions(options.ClickEventChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,

            // One background writer drains this channel; every request thread writes to it.
            SingleReader = true,
            SingleWriter = false,

            // Never resume the drain loop on a request thread: the redirect must not pay for the batch.
            AllowSynchronousContinuations = false,
        };

        _channel = Channel.CreateBounded<ClickEvent>(channelOptions, OnItemDropped);
    }

    /// <summary>
    /// The read side, consumed by <see cref="ClickEventBatchWriter"/>.
    /// </summary>
    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    /// <inheritdoc />
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc />
    public bool TryWrite(ClickEvent clickEvent)
    {
        ArgumentNullException.ThrowIfNull(clickEvent);

        t_dropped = false;

        bool accepted = _channel.Writer.TryWrite(clickEvent) && !t_dropped;

        t_dropped = false;
        return accepted;
    }

    /// <summary>
    /// Closes the channel so the drain loop can finish the events already queued and stop.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call closed the channel, <see langword="false"/> when it was
    /// already closed.
    /// </returns>
    public bool Complete() => _channel.Writer.TryComplete();

    private void OnItemDropped(ClickEvent dropped)
    {
        _ = dropped;

        t_dropped = true;
        _ = Interlocked.Increment(ref _droppedCount);
        _metrics.ClickEventDropped();
    }
}
