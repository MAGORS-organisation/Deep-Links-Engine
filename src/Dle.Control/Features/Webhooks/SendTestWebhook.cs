using System.Collections.ObjectModel;

using Dle.Control.Features.Shared;
using Dle.Domain.Entities;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// <c>POST /api/v1/webhooks/{id}/test</c> — sends one delivery now and reports what happened
/// (§B.7.3, FR-204).
/// </summary>
/// <remarks>
/// <para>
/// The integration story for webhooks is uniquely bad without this. A customer registers an
/// endpoint, waits for an event that may not occur for hours, and when nothing arrives has no way
/// to tell a firewall rule from a signature mistake from a subscription typo. This endpoint
/// collapses that loop into one request: it sends a real, fully signed delivery through the same
/// dispatcher the worker uses and hands back the status code, the error and the exact body that
/// was signed.
/// </para>
/// <para>
/// It is synchronous and deliberately does not go through the outbox. A queued test would be
/// answered 202 and the result would land in a log the customer has not built yet, which is the
/// same dead end the test is meant to remove. The delivery is still recorded against the
/// subscription, so it shows up in the delivery listing next to the real ones.
/// </para>
/// <para>
/// The payload is a genuine envelope of type <see cref="WebhookEventTypes.Test"/>, so a receiver
/// that switches on the event type can ignore it in production without special casing anything.
/// </para>
/// </remarks>
public static class SendTestWebhook
{
    /// <summary>
    /// Handles a test delivery.
    /// </summary>
    /// <param name="subscriptionId">The subscription to test.</param>
    /// <param name="store">Tenant scoped subscription lookup.</param>
    /// <param name="dispatcher">The signer and sender.</param>
    /// <param name="keys">Webhook signing keys, so the response names the published key.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the outcome, or 404 when there is no such subscription here.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        Guid subscriptionId,
        WebhookDeliveryStore store,
        WebhookDispatcher dispatcher,
        WebhookSigningKeys keys,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(timeProvider);

        WebhookSubscription? subscription = await store.FindAsync(subscriptionId, cancellationToken);

        if (subscription is null)
        {
            // Also the answer for a subscription of another tenant (SHARED-KERNEL §17.7, TC-166).
            return DleProblemResults.NotFound("No such webhook subscription.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid deliveryId = Guid.CreateVersion7(now);

        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["subscription_id"] = subscription.Id.ToString(),
            ["message"] = "This is a test delivery produced by the /test endpoint. No event occurred.",
        };

        string payload = WebhookEventTypes.Render(
            WebhookEventTypes.Test,
            deliveryId,
            now,
            new ReadOnlyDictionary<string, string>(data));

        WebhookAttempt attempt = await dispatcher.SendAsync(
            subscription.Url,
            subscription.SecretEncrypted,
            WebhookEventTypes.Test,
            payload,
            cancellationToken);

        string? kid = null;

        try
        {
            kid = (await keys.GetSignerAsync(cancellationToken)).KeyId;
        }
        catch (InvalidOperationException)
        {
            // The dispatcher has already reported this as unsendable; the response simply has no
            // key identifier to name.
            kid = null;
        }

        TestWebhookResponse response = new()
        {
            Delivered = attempt.Succeeded,
            Outcome = attempt.Outcome,
            ResponseCode = attempt.StatusCode,
            Error = attempt.Error,
            ElapsedMs = (int)Math.Min(attempt.Elapsed.TotalMilliseconds, int.MaxValue),
            Payload = payload,
            SigningKeyId = kid,
        };

        return TypedResults.Json(response, WebhookJsonContext.Default.TestWebhookResponse);
    }
}
