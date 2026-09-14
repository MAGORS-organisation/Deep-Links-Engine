using Dle.Domain.Analytics;

namespace Dle.Domain.Ports;

/// <summary>
/// Writes batches of click events drained from <see cref="IClickEventSink"/> to durable storage
/// (§C.3.2).
/// </summary>
public interface IClickEventWriter
{
    /// <summary>Writes one batch.</summary>
    /// <param name="batch">The events to write, in the order they were produced.</param>
    /// <param name="ct">Cancellation token. On shutdown the writer is expected to flush what it
    /// already holds before honouring it.</param>
    /// <returns>A task that completes when the batch is durable.</returns>
    Task WriteBatchAsync(IReadOnlyList<ClickEvent> batch, CancellationToken ct);
}
