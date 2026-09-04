using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// <c>GET /api/v1/webhooks</c> and <c>GET /api/v1/webhooks/deliveries</c> — what the tenant has
/// registered, and what happened to it (FR-204, §B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// Neither listing ever includes a shared secret. The column is encrypted at rest and there is no
/// read path for it at all, which is the point: a listing endpoint is exactly the kind of place
/// where a field gets added "for convenience" and quietly turns an encrypted column into a
/// plaintext one.
/// </para>
/// <para>
/// The delivery listing is the integrator's debugger. When a customer says "we are not getting
/// callbacks", the answer is in the status, the attempt count, the response code and the error of
/// the recent rows — and having it in the API rather than in the operator's log is what keeps the
/// operator out of the conversation.
/// </para>
/// </remarks>
public static class ListWebhooks
{
    /// <summary>Lists the subscriptions of the tenant in scope.</summary>
    /// <param name="onlyActive">Whether inactive subscriptions are hidden.</param>
    /// <param name="webhooks">Subscription storage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the subscriptions, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="webhooks"/> is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        bool onlyActive,
        WebhookRepository webhooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhooks);

        IReadOnlyList<WebhookSubscription> subscriptions =
            await webhooks.ListSubscriptionsAsync(onlyActive, cancellationToken);

        List<WebhookResponse> response = new(subscriptions.Count);

        foreach (WebhookSubscription subscription in subscriptions)
        {
            response.Add(new WebhookResponse
            {
                Id = subscription.Id,
                Url = subscription.Url,
                EventTypes = subscription.EventTypes,
                IsActive = subscription.IsActive,
                CreatedAt = subscription.CreatedAt,
            });
        }

        return TypedResults.Json(response, WebhookJsonContext.Default.ListWebhookResponse);
    }

    /// <summary>Lists recent delivery attempts of the tenant in scope.</summary>
    /// <param name="subscriptionId">Restrict to one subscription, or <see langword="null"/>.</param>
    /// <param name="limit">Maximum rows, bounded by configuration.</param>
    /// <param name="includePayload">Whether the sent body is included.</param>
    /// <param name="webhooks">Delivery storage.</param>
    /// <param name="options">Webhook options, for the page bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the deliveries, newest first.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleDeliveriesAsync(
        Guid? subscriptionId,
        int? limit,
        bool includePayload,
        WebhookRepository webhooks,
        IOptionsMonitor<WebhookOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(options);

        int maximum = options.CurrentValue.MaxDeliveryPageSize;
        int take = Math.Clamp(limit ?? maximum, 1, maximum);

        IReadOnlyList<WebhookDelivery> deliveries =
            await webhooks.ListDeliveriesAsync(subscriptionId, take, cancellationToken);

        List<WebhookDeliveryResponse> response = new(deliveries.Count);

        foreach (WebhookDelivery delivery in deliveries)
        {
            response.Add(new WebhookDeliveryResponse
            {
                Id = delivery.Id,
                SubscriptionId = delivery.SubscriptionId,
                EventType = delivery.EventType,
                Status = delivery.Status,
                Attempt = delivery.Attempt,
                ResponseCode = delivery.ResponseCode,
                LastError = delivery.LastError,
                NextAttemptAt = delivery.NextAttemptAt,
                CreatedAt = delivery.CreatedAt,
                DeliveredAt = delivery.DeliveredAt,
                Payload = includePayload ? delivery.Payload : null,
            });
        }

        return TypedResults.Json(response, WebhookJsonContext.Default.ListWebhookDeliveryResponse);
    }
}
