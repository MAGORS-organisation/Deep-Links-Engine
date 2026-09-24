using Dle.Domain.Analytics;

namespace Dle.Domain.Ports;

/// <summary>
/// Writes batches of SDK reported events (<c>POST /v1/events</c>, §B.7.2).
/// </summary>
public interface ISdkEventWriter
{
    /// <summary>Writes one batch.</summary>
    /// <param name="batch">The events to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the batch is durable. The endpoint answers 202 before
    /// this completes, so the implementation owns the durability guarantee, not the caller.</returns>
    Task WriteBatchAsync(IReadOnlyList<SdkEvent> batch, CancellationToken ct);
}
