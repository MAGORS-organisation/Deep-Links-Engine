namespace Dle.Domain.Ports;

/// <summary>
/// Hands a postback to the delivery worker (FR-204, §B.7.4).
/// </summary>
/// <remarks>
/// Enqueuing is durable and delivery is asynchronous, with exponential backoff and a dead letter
/// queue. A customer endpoint that is slow or down must never hold up the request that produced
/// the event.
/// </remarks>
public interface IWebhookOutbox
{
    /// <summary>Enqueues one event for delivery to every active subscription of the tenant.</summary>
    /// <param name="tenantId">Tenant the event belongs to.</param>
    /// <param name="eventType">Event type, for example <c>attribution.created</c>.</param>
    /// <param name="payloadJson">Serialized payload, already in its final form so that the
    /// signature covers exactly the bytes that will be sent (TC-165).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the event is durably queued.</returns>
    Task EnqueueAsync(Guid tenantId, string eventType, string payloadJson, CancellationToken ct);
}
