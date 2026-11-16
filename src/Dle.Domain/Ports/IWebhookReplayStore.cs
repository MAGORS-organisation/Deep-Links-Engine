namespace Dle.Domain.Ports;

/// <summary>
/// Outcome of attempting to re-queue one webhook delivery for redelivery.
/// </summary>
public enum WebhookReplayStatus
{
    /// <summary>The delivery was reset to pending for immediate pickup.</summary>
    Replayed = 0,

    /// <summary>No delivery with that identifier exists in the caller's tenant.</summary>
    NotFound = 1,

    /// <summary>The delivery was already accepted by the destination endpoint.</summary>
    AlreadyDelivered = 2,

    /// <summary>The parent subscription is missing or inactive.</summary>
    SubscriptionInactive = 3,
}

/// <summary>
/// Result of re-enqueueing one webhook delivery.
/// </summary>
/// <param name="Status">Whether the delivery was queued or why it could not be.</param>
/// <param name="DeliveryId">Identifier of the queued delivery when replayed.</param>
public sealed record WebhookSingleReplayResult(
    WebhookReplayStatus Status,
    Guid? DeliveryId = null);

/// <summary>
/// Result of re-enqueueing dead webhook deliveries in bulk.
/// </summary>
/// <param name="DeliveryIds">Identifiers of the deliveries moved back to pending.</param>
public sealed record WebhookBatchReplayResult(IReadOnlyList<Guid> DeliveryIds);

/// <summary>
/// Re-queues failed or dead webhook deliveries within the caller's tenant scope.
/// </summary>
public interface IWebhookReplayStore
{
    /// <summary>
    /// Resets one delivery in the caller's tenant back to pending for immediate delivery.
    /// </summary>
    /// <param name="deliveryId">The delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the replay request.</returns>
    Task<WebhookSingleReplayResult> ReplayDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resets dead deliveries in the caller's tenant back to pending for active subscriptions.
    /// </summary>
    /// <param name="subscriptionId">Restrict to one subscription, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum rows to re-queue in one batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The re-queued delivery identifiers.</returns>
    Task<WebhookBatchReplayResult> ReplayDeadDeliveriesAsync(
        Guid? subscriptionId,
        int limit,
        CancellationToken cancellationToken);
}
