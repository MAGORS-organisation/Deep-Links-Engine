using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Dle.Control.Features.Shared;
using Dle.Control.Features.Webhooks;
using Dle.Control.Identity;
using Dle.Domain.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The webhook management endpoints (SHARED-KERNEL §15, §B.7.3, FR-204).
/// </summary>
/// <remarks>
/// <para>
/// Authorization uses the credential policies rather than a policy of its own, and the choice is
/// deliberate: registering a subscription mints a shared secret, so it sits at the same level as
/// issuing an API key (<see cref="DlePolicies.KeysWrite"/>, owner), while listing subscriptions and
/// deliveries and pressing the test button sit at the level of reading them
/// (<see cref="DlePolicies.KeysRead"/>, admin). A dedicated <c>webhooks:*</c> scope would be a
/// better fit and is a deliberate omission: adding a policy name that
/// <c>AddDleIdentity</c> does not register would fail closed at request time, and a webhook route
/// that 500s is worse than one that is guarded a notch too tightly.
/// </para>
/// <para>
/// Bodies are read through the module's own source generated context rather than through the
/// framework binder, so the wire format of this module stays snake_case whatever the host
/// configured globally, and so a payload cannot be shaped by another slice's serializer options.
/// </para>
/// </remarks>
public static class WebhookEndpointExtensions
{
    /// <summary>Largest management request body accepted, in bytes.</summary>
    private const int MaxRequestBytes = 32 * 1024;

    /// <summary>
    /// Maps the webhook subscription and delivery routes under <c>/api/v1/webhooks</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/webhooks")
            .WithTags("Webhooks");

        group.MapGet("", static (
                bool? onlyActive,
                Dle.Persistence.Repositories.WebhookRepository webhooks,
                CancellationToken cancellationToken) =>
                ListWebhooks.HandleAsync(onlyActive ?? false, webhooks, cancellationToken))
            .RequireAuthorization(DlePolicies.KeysRead)
            .WithName("ListWebhooks")
            .WithSummary("Lists the tenant's webhook subscriptions.")
            .WithDescription("The shared secret is never returned; it is shown once, at creation.")
            .Produces<List<WebhookResponse>>(StatusCodes.Status200OK, "application/json");

        group.MapPost("", CreateAsync)
            .RequireAuthorization(DlePolicies.KeysWrite)
            .WithName("CreateWebhook")
            .WithSummary("Registers a postback endpoint.")
            .WithDescription(
                "The destination is resolved and checked against the same target policy a link is "
                + "checked against, so a webhook cannot be aimed at a private, loopback or cloud "
                + "metadata address (T-02). The response carries the shared secret exactly once.")
            .Accepts<CreateWebhookRequest>("application/json")
            .Produces<WebhookCreatedResponse>(StatusCodes.Status201Created, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", static (
                Guid id,
                HttpContext context,
                Dle.Persistence.Repositories.WebhookRepository webhooks,
                Dle.Persistence.Repositories.AuditLogWriter audit,
                CancellationToken cancellationToken) =>
                DeleteWebhook.HandleAsync(id, context, webhooks, audit, cancellationToken))
            .RequireAuthorization(DlePolicies.KeysWrite)
            .WithName("DeleteWebhook")
            .WithSummary("Stops delivering to a subscription.")
            .WithDescription(
                "Deactivation rather than deletion: the delivery history stays, because it is the "
                + "only answer to a later question about what was and was not sent.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", static (
                Guid id,
                WebhookDeliveryStore store,
                WebhookDispatcher dispatcher,
                WebhookSigningKeys keys,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
                SendTestWebhook.HandleAsync(id, store, dispatcher, keys, timeProvider, cancellationToken))
            .RequireAuthorization(DlePolicies.KeysRead)
            .WithName("TestWebhook")
            .WithSummary("Sends one signed test delivery and reports the result.")
            .WithDescription(
                "A real, fully signed delivery of type webhook.test through the same dispatcher the "
                + "worker uses. Synchronous, so the integrator learns immediately whether the "
                + "endpoint answered and what it answered.")
            .Produces<TestWebhookResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/deliveries", static (
                Guid? subscriptionId,
                int? limit,
                bool? includePayload,
                Dle.Persistence.Repositories.WebhookRepository webhooks,
                IOptionsMonitor<WebhookOptions> options,
                CancellationToken cancellationToken) =>
                ListWebhooks.HandleDeliveriesAsync(
                    subscriptionId,
                    limit,
                    includePayload ?? false,
                    webhooks,
                    options,
                    cancellationToken))
            .RequireAuthorization(DlePolicies.KeysRead)
            .WithName("ListWebhookDeliveries")
            .WithSummary("Lists recent delivery attempts.")
            .WithDescription(
                "The integrator's debugger: status, attempt count, response code and error for the "
                + "recent deliveries, optionally with the exact body that was signed and sent.")
            .Produces<List<WebhookDeliveryResponse>>(StatusCodes.Status200OK, "application/json");

        return app;
    }

    /// <summary>Handles <c>POST /api/v1/webhooks</c>, reading the body through the module's context.</summary>
    private static async Task<IResult> CreateAsync(
        HttpContext context,
        Dle.Persistence.Repositories.WebhookRepository webhooks,
        WebhookDeliveryStore store,
        WebhookSecretProtector secrets,
        WebhookSigningKeys keys,
        Dle.Persistence.Repositories.AuditLogWriter audit,
        IOptionsMonitor<WebhookOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        (CreateWebhookRequest? request, IResult? problem) = await ReadAsync(
            context,
            WebhookJsonContext.Default.CreateWebhookRequest,
            cancellationToken);

        if (problem is not null || request is null)
        {
            return problem ?? DleProblemResults.ValidationFailed("A JSON object is required.");
        }

        return await CreateWebhook.HandleAsync(
            request,
            context,
            webhooks,
            store,
            secrets,
            keys,
            audit,
            options,
            timeProvider,
            cancellationToken);
    }

    /// <summary>Reads a bounded JSON body through a source generated type.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="typeInfo">Source generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or the problem document explaining why there is none.</returns>
    private static async Task<(T? Value, IResult? Problem)> ReadAsync<T>(
        HttpContext context,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is { } declared && declared > MaxRequestBytes)
        {
            return (default, DleProblemResults.Create(
                StatusCodes.Status413PayloadTooLarge,
                ProblemCodes.ValidationFailed,
                "The request body is too large.",
                "A webhook management request body must not exceed 32 KiB."));
        }

        IHttpMaxRequestBodySizeFeature? sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxRequestBytes;
        }

        try
        {
            T? value = await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, cancellationToken);

            return (value, null);
        }
        catch (JsonException)
        {
            // The parser's message quotes the offending input; it is not echoed back
            // (SHARED-KERNEL §17.5).
            return (default, DleProblemResults.ValidationFailed("The request body is not valid JSON."));
        }
        catch (BadHttpRequestException)
        {
            return (default, DleProblemResults.Create(
                StatusCodes.Status413PayloadTooLarge,
                ProblemCodes.ValidationFailed,
                "The request body is too large.",
                "A webhook management request body must not exceed 32 KiB."));
        }
    }
}
