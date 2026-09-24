using Dle.Domain.Analytics;

namespace Dle.Domain.Ports;

/// <summary>
/// Non blocking entry point to the click stream, backed by a bounded channel (NFR-06, FR-165).
/// </summary>
/// <remarks>
/// The response to a click must be delivered even when the analytics layer is down, so this sink
/// never blocks and never throws: when the channel is full the event is dropped and counted. That
/// is a deliberate trade — losing an analytics row is an inconvenience, while making a redirect
/// wait on a writer is an outage.
/// </remarks>
public interface IClickEventSink
{
    /// <summary>Offers an event to the channel.</summary>
    /// <param name="clickEvent">The event to write.</param>
    /// <returns><see langword="false"/> when the channel was full and the event was dropped. The
    /// caller must continue serving the request regardless of the result.</returns>
    bool TryWrite(ClickEvent clickEvent);

    /// <summary>Number of events dropped since the process started. Exported as a metric: a
    /// rising value is the signal that the writer cannot keep up.</summary>
    long DroppedCount { get; }
}
