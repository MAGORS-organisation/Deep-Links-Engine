using System.Globalization;

using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// <c>POST /api/v1/webhooks/deliveries/{id}/replay</c> and
/// <c>POST /api/v1/webhooks/deliveries/replay-dead</c> — re-queues failed or dead webhook
/// deliveries for immediate retry without regenerating their signed payload.
/// </summary>
public static class ReplayWebhookDelivery
{
    /// <summary>Re-queues one webhook delivery in the caller's tenant scope.</summary>
    /// <param name="deliveryId">The delivery identifier.</param>
    /// <param name="context">The HTTP request, for caller identity.</param>
    /// <param name="store">Tenant-scoped delivery replay store.</param>
    /// <param name="audit">Administrative audit trail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// 200 with the queued identifier, 404 when no matching delivery exists in the tenant, or 409
    /// when the delivery already succeeded or its subscription is inactive.
    /// </returns>
    public static async Task<IResult> HandleSingleAsync(
        Guid deliveryId,
        HttpContext context,
        IWebhookReplayStore store,
        IAuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = context.RequireDleCaller();

        WebhookSingleReplayResult result = await store.ReplayDeliveryAsync(deliveryId, cancellationToken);

        switch (result.Status)
        {
            case WebhookReplayStatus.NotFound:
                return DleProblemResults.NotFound("No such webhook delivery.");

            case WebhookReplayStatus.SubscriptionInactive:
                return DleProblemResults.Conflict(
                    ProblemCodes.WriteConflict,
                    "The webhook subscription is inactive and cannot receive replayed deliveries.");

            case WebhookReplayStatus.AlreadyDelivered:
                return DleProblemResults.Conflict(
                    ProblemCodes.WriteConflict,
                    "The webhook delivery was already accepted by the destination endpoint.");
        }

        IReadOnlyList<Guid> ids = result.DeliveryId is { } id ? [id] : [];

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["mode"] = "single",
            ["replayed_count"] = ids.Count.ToString(CultureInfo.InvariantCulture),
        };

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.WebhookDeliveryReplayed,
            AuditActions.WebhookSubject,
            deliveryId.ToString(),
            metadata,
            cancellationToken);

        WebhookDeliveryReplayResponse response = new()
        {
            ReplayedCount = ids.Count,
            DeliveryIds = ids,
        };

        return TypedResults.Json(response, WebhookJsonContext.Default.WebhookDeliveryReplayResponse);
    }

    /// <summary>Re-queues dead-letter webhook deliveries for active subscriptions in scope.</summary>
    /// <param name="subscriptionId">Restrict to one subscription, or <see langword="null"/> for all.</param>
    /// <param name="limit">Maximum rows to re-queue in one call.</param>
    /// <param name="context">The HTTP request, for caller identity.</param>
    /// <param name="store">Tenant-scoped delivery replay store.</param>
    /// <param name="options">Webhook options, for page bounds.</param>
    /// <param name="audit">Administrative audit trail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the count and identifiers queued.</returns>
    public static async Task<IResult> HandleDeadAsync(
        Guid? subscriptionId,
        int? limit,
        HttpContext context,
        IWebhookReplayStore store,
        IOptionsMonitor<WebhookOptions> options,
        IAuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(audit);

        DleCaller caller = context.RequireDleCaller();

        int maximum = Math.Clamp(options.CurrentValue.MaxDeliveryPageSize, 1, 500);
        int take = Math.Clamp(limit ?? maximum, 1, maximum);

        WebhookBatchReplayResult result = await store.ReplayDeadDeliveriesAsync(
            subscriptionId,
            take,
            cancellationToken);

        if (result.DeliveryIds.Count > 0)
        {
            Dictionary<string, string> metadata = new(StringComparer.Ordinal)
            {
                ["mode"] = "dead_batch",
                ["replayed_count"] = result.DeliveryIds.Count.ToString(CultureInfo.InvariantCulture),
            };

            string subjectId = subscriptionId?.ToString() ?? caller.TenantId.ToString();

            await AdministrativeAudit.RecordAsync(
                audit,
                caller,
                AuditActions.WebhookDeliveryReplayed,
                AuditActions.WebhookSubject,
                subjectId,
                metadata,
                cancellationToken);
        }

        WebhookDeliveryReplayResponse response = new()
        {
            ReplayedCount = result.DeliveryIds.Count,
            DeliveryIds = result.DeliveryIds,
        };

        return TypedResults.Json(response, WebhookJsonContext.Default.WebhookDeliveryReplayResponse);
    }
}
