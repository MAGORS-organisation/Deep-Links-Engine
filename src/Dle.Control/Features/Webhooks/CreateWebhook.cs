using System.Security.Cryptography;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// <c>POST /api/v1/webhooks</c> — registers a postback endpoint (FR-204, §B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// Creation is where the destination is judged. A URL that resolves to a private, loopback,
/// link-local or metadata address is refused here with a message, rather than accepted and then
/// failing silently on every delivery — and rather than, far worse, succeeding and turning the
/// control plane into a proxy into its own network (T-02).
/// </para>
/// <para>
/// The shared secret is generated here and returned exactly once. The stored copy is encrypted,
/// and there is deliberately no endpoint that reads it back: a secret that can be re-read is a
/// secret that leaks through the next read path somebody adds.
/// </para>
/// </remarks>
public static class CreateWebhook
{
    /// <summary>Audit action recorded when a subscription is registered.</summary>
    public const string AuditAction = "webhook.created";

    /// <summary>
    /// Handles a registration.
    /// </summary>
    /// <param name="request">The requested subscription.</param>
    /// <param name="context">The request, for the authenticated caller.</param>
    /// <param name="webhooks">Subscription storage.</param>
    /// <param name="store">Tenant scoped reads, for the quota check.</param>
    /// <param name="secrets">Protector of the shared secret.</param>
    /// <param name="keys">Webhook signing keys, so the response can name the published key.</param>
    /// <param name="audit">Audit trail (FR-246).</param>
    /// <param name="options">Webhook options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the subscription and its secret, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        CreateWebhookRequest request,
        HttpContext context,
        WebhookRepository webhooks,
        WebhookDeliveryStore store,
        WebhookSecretProtector secrets,
        WebhookSigningKeys keys,
        AuditLogWriter audit,
        IOptionsMonitor<WebhookOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleCaller caller = context.RequireDleCaller();
        WebhookOptions current = options.CurrentValue;

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        WebhookDestinationVerdict destination = await WebhookDestinationPolicy.ValidateAsync(
            request.Url,
            current.AllowPrivateDestinations,
            cancellationToken);

        if (!destination.Accepted)
        {
            errors["url"] = [destination.Reason ?? "The destination is not a permitted target."];
        }

        if (!WebhookDestinationPolicy.TryNormalizeEventTypes(
                request.EventTypes,
                out List<string> eventTypes,
                out string? eventTypeError))
        {
            errors["event_types"] = [eventTypeError ?? "The event types are not valid."];
        }

        if (errors.Count > 0)
        {
            return DleProblemResults.ValidationFailed("The subscription could not be registered.", errors);
        }

        int existing = await store.CountAsync(cancellationToken);

        if (existing >= current.MaxSubscriptionsPerTenant)
        {
            // A soft quota in the sense of §E.9: it blocks creating another subscription and never
            // blocks a delivery of one that already exists.
            return DleProblemResults.Conflict(
                ProblemCodes.RateLimited,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This tenant already has {existing} webhook subscriptions, which is the configured maximum."));
        }

        byte[] secret = WebhookSecretProtector.NewSecret();
        DateTimeOffset now = timeProvider.GetUtcNow();

        WebhookSubscription subscription = await webhooks.AddSubscriptionAsync(
            new WebhookSubscription
            {
                Id = Guid.CreateVersion7(now),
                TenantId = caller.TenantId,
                Url = request.Url.Trim(),
                SecretEncrypted = secrets.Protect(secret),
                EventTypes = eventTypes,
                IsActive = request.IsActive ?? true,
                CreatedAt = now,
            },
            cancellationToken);

        RequestActor actor = RequestActor.FromPrincipal(context.User);

        // The audit entry names the destination host and the event types, never the secret and
        // never an end user identifier (§E.6.3).
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["url_host"] = HostOf(subscription.Url),
            ["event_types"] = string.Join(",", eventTypes),
            ["is_active"] = subscription.IsActive ? "true" : "false",
        };

        _ = await audit.WriteAsync(
            AuditAction,
            "webhook",
            subscription.Id.ToString(),
            actor.ActorType,
            actor.ActorId,
            JsonSerializer.Serialize(metadata, DleDomainJsonContext.Default.DictionaryStringString),
            cancellationToken);

        string? kid = null;

        try
        {
            kid = (await keys.GetSignerAsync(cancellationToken)).KeyId;
        }
        catch (InvalidOperationException)
        {
            // No asymmetric key is available yet. The subscription is still valid — the v1 slot
            // works — and the response simply omits the key identifier rather than failing the
            // registration over a detail the integrator can look up at /.well-known/jwks.json.
            kid = null;
        }

        WebhookCreatedResponse response = new()
        {
            Id = subscription.Id,
            Url = subscription.Url,
            EventTypes = eventTypes,
            IsActive = subscription.IsActive,
            CreatedAt = subscription.CreatedAt,
            Secret = WebhookSecretProtector.Render(secret),
            SigningKeyId = kid,
        };

        CryptographicOperations.ZeroMemory(secret);

        return TypedResults.Json(
            response,
            WebhookJsonContext.Default.WebhookCreatedResponse,
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Extracts the host of a destination, for the audit entry.</summary>
    /// <param name="url">The stored destination URL.</param>
    /// <returns>The host, or <c>unknown</c>.</returns>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed.Host : "unknown";
}
