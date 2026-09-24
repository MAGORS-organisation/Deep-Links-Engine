using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// <c>DELETE /api/v1/webhooks/{id}</c> — stops delivering to a subscription (FR-204).
/// </summary>
/// <remarks>
/// <para>
/// Deactivation, not deletion. The row stays, so the delivery history stays with it and a customer
/// asking "did you ever send us the conversions for that campaign?" can be answered from the
/// record rather than from memory. It is the same reasoning quarantine uses for abused links: quiet
/// removal destroys exactly the evidence that will be asked for later (§E.3 step 8).
/// </para>
/// <para>
/// A subscription of another tenant answers 404, not 403. The tenant filter means the row is not in
/// the queryable at all, so the two cases are indistinguishable inside the process and therefore
/// indistinguishable from outside (SHARED-KERNEL §17.7, TC-166).
/// </para>
/// </remarks>
public static class DeleteWebhook
{
    /// <summary>Audit action recorded when a subscription is deactivated.</summary>
    public const string AuditAction = "webhook.deactivated";

    /// <summary>
    /// Handles a deactivation.
    /// </summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="context">The request, for the authenticated caller.</param>
    /// <param name="webhooks">Subscription storage.</param>
    /// <param name="audit">Audit trail (FR-246).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 when it was deactivated, 404 when there is no such subscription here.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        Guid subscriptionId,
        HttpContext context,
        WebhookRepository webhooks,
        AuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(audit);

        bool deactivated = await webhooks.DeactivateSubscriptionAsync(subscriptionId, cancellationToken);

        if (!deactivated)
        {
            return DleProblemResults.NotFound("No such webhook subscription.");
        }

        RequestActor actor = RequestActor.FromPrincipal(context.User);

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["result"] = "deactivated",
        };

        _ = await audit.WriteAsync(
            AuditAction,
            "webhook",
            subscriptionId.ToString(),
            actor.ActorType,
            actor.ActorId,
            JsonSerializer.Serialize(metadata, DleDomainJsonContext.Default.DictionaryStringString),
            cancellationToken);

        return TypedResults.NoContent();
    }
}
