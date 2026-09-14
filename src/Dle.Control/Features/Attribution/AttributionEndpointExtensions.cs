using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;

using Dle.Control.Features.Attribution;
using Dle.Control.Identity;
using Dle.Domain.Attribution;
using Dle.Domain.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The SDK facing endpoints (SHARED-KERNEL §15, §B.7.2). Component C-06 of §B.3.
/// </summary>
/// <remarks>
/// <para>
/// Three routes, and they are deliberately not under <c>/api/v1</c>. The control plane API is
/// operated by people with credentials that can change configuration; these are called by an
/// application binary on a device, with a key that anybody who downloads the application can
/// extract. Different prefix, different authentication scheme, different limits — the separation
/// is §E.2.1 trust boundary TB2 made structural.
/// </para>
/// <para>
/// Both SDK routes read and write through the source generated
/// <see cref="AttributionJsonContext"/> rather than through the framework's binder. That is what
/// SHARED-KERNEL §17.3 asks for on a path that is allowed sixty calls a minute per installation,
/// and it also pins the wire format to snake_case in this module regardless of what any other
/// slice configures globally.
/// </para>
/// <para>
/// Both take their rate limit lease <em>after</em> the body is parsed, because §E.9 keys both
/// limits on <c>install_id</c> and that value arrives in the body. The rate limiting middleware
/// runs long before a body is read, so a middleware policy would have to buffer and parse every
/// request just to find its partition key — exactly the work the limit exists to prevent.
/// </para>
/// </remarks>
public static class AttributionEndpointExtensions
{
    /// <summary>Longest installation identifier that is even looked at, before options are read.</summary>
    private const int AbsoluteMaxInstallIdLength = 512;

    /// <summary>
    /// Maps <c>POST /v1/resolve</c>, <c>POST /v1/events</c> and <c>POST /v1/claim-codes</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapAttribution(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The SDK routes carry their own §E.9 limiters, keyed on install_id and acquired inside the
        // handlers because that value arrives in the body. The middleware's safety net is switched
        // off for them deliberately: it partitions by credential, and one SDK key is shared by every
        // installation of an application, so a tenant-wide request budget would throttle a hundred
        // thousand devices against a limit §E.9 defines per device.
        RouteGroupBuilder sdk = app.MapGroup("/v1")
            .WithTags("SDK")
            .DisableRateLimiting();

        sdk.MapPost("/resolve", ResolveAsync)
            .RequireAuthorization(DlePolicies.SdkIngest)
            .WithName("ResolveInstall")
            .WithSummary("Recovers the click context of a fresh installation.")
            .WithDescription(
                "Called once on the first launch after an install. Answers with the strategy that "
                + "produced the match and how much to trust it; an unmatched install is a 200 with "
                + "matched=false, not an error. Limited to five calls an hour per install_id (§E.9).")
            .Accepts<ResolveRequestDto>("application/json")
            .Produces<ResolveResponseDto>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sdk.MapPost("/events", RecordAsync)
            .RequireAuthorization(DlePolicies.SdkIngest)
            .WithName("RecordSdkEvents")
            .WithSummary("Records a batch of client events, link opens included.")
            .WithDescription(
                "Batched, at most 100 events per request. The link_open event is what makes a "
                + "direct open through a verified Universal or App Link measurable at all: the "
                + "operating system opens the application without any request reaching the engine, "
                + "so without this report the most successful campaigns are the most under-counted "
                + "(§B.6.4, FR-223). A link_open whose URL resolves to one of the caller's links "
                + "creates a direct_open attribution with confidence 1.00.")
            .Accepts<EventBatchDto>("application/json")
            .Produces<EventBatchAcceptedDto>(StatusCodes.Status202Accepted, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sdk.MapPost("/claim-codes", IssueClaimCodeAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .WithName("IssueClaimCode")
            .WithSummary("Issues the six characters an interstitial page shows the user.")
            .WithDescription(
                "The deterministic iOS path of ADR-008. The edge has just written a click event "
                + "and exchanges its identifier for a short lived, single use code the user can "
                + "read off the screen and type into the application (FR-184, §B.6.3). Only the "
                + "keyed hash of the code is stored; the plaintext exists in this response and "
                + "nowhere else.")
            .Accepts<IssueClaimCodeRequest>("application/json")
            .Produces<ClaimCodeResponse>(StatusCodes.Status201Created, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>Handles <c>POST /v1/resolve</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="resolve">The engine.</param>
    /// <param name="limiters">The §E.9 limiters.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="limits">The configured §E.9 limits, quoted back in the refusal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, or a problem document.</returns>
    private static async Task<IResult> ResolveAsync(
        HttpContext context,
        ResolveInstall resolve,
        AttributionRateLimiters limiters,
        AttributionMetrics metrics,
        IOptionsMonitor<AttributionOptions> options,
        IOptionsMonitor<AttributionRateLimitOptions> limits,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetSdkCaller(out SdkCaller caller))
        {
            metrics.Rejected("unauthenticated");
            return AttributionProblem.Unauthenticated();
        }

        AttributionOptions current = options.CurrentValue;

        BodyResult<ResolveRequestDto> body = await ReadBodyAsync(
            context,
            current.MaxRequestBytes,
            AttributionJsonContext.Default.ResolveRequestDto,
            cancellationToken);

        if (body.Problem is { } problem)
        {
            return problem;
        }

        ResolveRequestDto request = body.Value!;

        if (!TryValidateInstallId(request.InstallId, current.MaxInstallIdLength, out IResult? invalid))
        {
            return invalid;
        }

        using RateLimitLease lease = await limiters.AcquireResolveAsync(request.InstallId, cancellationToken);

        if (!lease.IsAcquired)
        {
            metrics.Rejected("rate_limited");

            AttributionRateLimitOptions configured = limits.CurrentValue;

            return AttributionProblem.RateLimited(
                AttributionRateLimiters.RetryAfter(lease),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An installation may resolve {configured.ResolvePermitLimit} times per {configured.ResolveWindowMinutes} minutes (§E.9); a correct SDK calls this endpoint once in the lifetime of an installation."));
        }

        ResolveOutcome outcome = await resolve.ExecuteAsync(
            request,
            caller,
            context.Connection.RemoteIpAddress,
            cancellationToken);

        if (outcome.ClaimCodeError is { } status)
        {
            return ClaimCodeProblem(status);
        }

        return outcome.Response is { } response
            ? TypedResults.Json(response, AttributionJsonContext.Default.ResolveResponseDto)
            : AttributionProblem.DependencyUnavailable(
                "The attribution store did not answer; the installation was neither matched nor recorded.");
    }

    /// <summary>Handles <c>POST /v1/events</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="events">The use case.</param>
    /// <param name="limiters">The §E.9 limiters.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="limits">The configured §E.9 limits, which bound the batch size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with the accepted and rejected counts, or a problem document.</returns>
    private static async Task<IResult> RecordAsync(
        HttpContext context,
        RecordEvents events,
        AttributionRateLimiters limiters,
        AttributionMetrics metrics,
        IOptionsMonitor<AttributionOptions> options,
        IOptionsMonitor<AttributionRateLimitOptions> limits,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetSdkCaller(out SdkCaller caller))
        {
            metrics.Rejected("unauthenticated");
            return AttributionProblem.Unauthenticated();
        }

        AttributionOptions current = options.CurrentValue;

        BodyResult<EventBatchDto> body = await ReadBodyAsync(
            context,
            current.MaxRequestBytes,
            AttributionJsonContext.Default.EventBatchDto,
            cancellationToken);

        if (body.Problem is { } problem)
        {
            return problem;
        }

        EventBatchDto batch = body.Value!;

        if (!TryValidateInstallId(batch.InstallId, current.MaxInstallIdLength, out IResult? invalid))
        {
            return invalid;
        }

        if (batch.Events.Count == 0)
        {
            return AttributionProblem.Validation("The batch must carry at least one event.");
        }

        if (batch.Events.Count > EventBatchDto.MaxEventsPerBatch)
        {
            return AttributionProblem.Validation(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A batch carries at most {EventBatchDto.MaxEventsPerBatch} events."));
        }

        AttributionRateLimitOptions configured = limits.CurrentValue;

        if (batch.Events.Count > configured.EventsBurstLimit)
        {
            // A batch larger than the bucket can ever hold could never be leased, so it is refused
            // here with an answer the SDK can act on rather than being handed to a limiter that
            // would throw (SHARED-KERNEL §17.9).
            metrics.Rejected("rate_limited");

            return AttributionProblem.RateLimited(
                TimeSpan.FromSeconds(configured.EventsPeriodSeconds),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A batch may carry at most {configured.EventsBurstLimit} events for this deployment; split it and retry."));
        }

        // One permit per event, not one per request. Sixty requests a minute carrying a hundred
        // events each would be six thousand events a minute, which is not the limit §E.9 intends.
        using RateLimitLease lease = await limiters.AcquireEventsAsync(
            batch.InstallId,
            batch.Events.Count,
            cancellationToken);

        if (!lease.IsAcquired)
        {
            metrics.Rejected("rate_limited");

            return AttributionProblem.RateLimited(
                AttributionRateLimiters.RetryAfter(lease),
                "The event budget of this installation is spent. Back off exponentially and retry.");
        }

        EventBatchOutcome outcome = await events.ExecuteAsync(batch, caller, cancellationToken);

        return TypedResults.Json(
            new EventBatchAcceptedDto
            {
                Accepted = outcome.Accepted,
                Rejected = outcome.Rejected,
            },
            AttributionJsonContext.Default.EventBatchAcceptedDto,
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>Handles <c>POST /v1/claim-codes</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="claimCodes">Claim code issuance.</param>
    /// <param name="limiters">The §E.9 limiters.</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>201 with the code, or a problem document.</returns>
    /// <remarks>
    /// The caller is the edge, presenting a control plane key with the weakest role there is. That
    /// is the right level: issuing a code creates no configuration and reveals nothing the caller
    /// did not already supply — it needs the click identifier to ask at all — while the code it
    /// produces is single use, short lived and stored only as a keyed hash.
    /// </remarks>
    private static async Task<IResult> IssueClaimCodeAsync(
        HttpContext context,
        ClaimCodeService claimCodes,
        AttributionRateLimiters limiters,
        IOptionsMonitor<AttributionOptions> options,
        CancellationToken cancellationToken)
    {
        DleCaller? caller = context.User.GetDleCaller();

        if (caller is null)
        {
            return AttributionProblem.Unauthenticated();
        }

        if (!claimCodes.Enabled)
        {
            return AttributionProblem.ClaimCodeInvalid(
                "disabled",
                "Claim codes are switched off for this deployment (Dle:Attribution:ClaimCode:Enabled).",
                canReissue: false);
        }

        AttributionOptions current = options.CurrentValue;

        BodyResult<IssueClaimCodeRequest> body = await ReadBodyAsync(
            context,
            current.MaxRequestBytes,
            AttributionJsonContext.Default.IssueClaimCodeRequest,
            cancellationToken);

        if (body.Problem is { } problem)
        {
            return problem;
        }

        IssueClaimCodeRequest request = body.Value!;

        if (string.IsNullOrWhiteSpace(request.ClickId) || request.ClickId.Length > 128)
        {
            return AttributionProblem.Validation("A click identifier of at most 128 characters is required.");
        }

        long? linkId = null;

        if (!string.IsNullOrWhiteSpace(request.LinkId))
        {
            if (!long.TryParse(request.LinkId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return AttributionProblem.Validation("The link identifier must be a 64 bit integer in decimal.");
            }

            linkId = parsed;
        }

        using RateLimitLease lease = await limiters.AcquireClaimCodeIssueAsync(
            caller.TenantId.ToString(),
            cancellationToken);

        if (!lease.IsAcquired)
        {
            return AttributionProblem.RateLimited(
                AttributionRateLimiters.RetryAfter(lease),
                "Too many claim codes have been issued for this tenant. Retry shortly.");
        }

        ClaimCodeResponse response = await claimCodes.IssueAsync(
            caller.TenantId,
            request.ClickId.Trim(),
            linkId,
            cancellationToken);

        return TypedResults.Json(
            response,
            AttributionJsonContext.Default.ClaimCodeResponse,
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>A parsed body, or the problem document that explains why there is none.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="Value">The parsed body when parsing succeeded.</param>
    /// <param name="Problem">The problem document when it did not.</param>
    private readonly record struct BodyResult<T>(T? Value, IResult? Problem);

    /// <summary>
    /// Reads and deserializes a bounded JSON body through the source generated context.
    /// </summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">Largest body accepted, from configuration.</param>
    /// <param name="typeInfo">Source generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or a problem document.</returns>
    /// <remarks>
    /// The limit is applied twice: once against <c>Content-Length</c>, which refuses an oversized
    /// body before a byte of it is read, and once on the request body feature, which catches a
    /// chunked body that lied about its size. Neither check trusts the other.
    /// </remarks>
    private static async Task<BodyResult<T>> ReadBodyAsync<T>(
        HttpContext context,
        int maxBytes,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return new BodyResult<T>(default, AttributionProblem.PayloadTooLarge(maxBytes));
        }

        IHttpMaxRequestBodySizeFeature? sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        try
        {
            T? value = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                typeInfo,
                cancellationToken);

            return value is null
                ? new BodyResult<T>(default, AttributionProblem.Validation("A JSON object is required."))
                : new BodyResult<T>(value, null);
        }
        catch (JsonException)
        {
            // The parser's message quotes the offending input, which is attacker controlled text;
            // it is deliberately not echoed back or logged (SHARED-KERNEL §17.5).
            return new BodyResult<T>(default, AttributionProblem.Validation("The request body is not valid JSON."));
        }
        catch (BadHttpRequestException)
        {
            return new BodyResult<T>(default, AttributionProblem.PayloadTooLarge(maxBytes));
        }
    }

    /// <summary>Validates the installation identifier every SDK call is keyed on.</summary>
    /// <param name="installId">The identifier as received.</param>
    /// <param name="maxLength">Longest identifier accepted, from configuration.</param>
    /// <param name="problem">The problem document when the identifier is unusable.</param>
    /// <returns><see langword="true"/> when the identifier may be used as a partition key.</returns>
    private static bool TryValidateInstallId(
        string? installId,
        int maxLength,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IResult? problem)
    {
        int bound = Math.Min(maxLength, AbsoluteMaxInstallIdLength);

        if (string.IsNullOrWhiteSpace(installId) || installId.Length > bound)
        {
            problem = AttributionProblem.Validation(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"install_id is required and must not exceed {bound} characters."));

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Turns a claim code failure into the problem document an SDK can branch on (TC-148).</summary>
    /// <param name="status">Why the code could not be redeemed.</param>
    /// <returns>The problem document.</returns>
    /// <remarks>
    /// <c>can_reissue</c> is the member that matters to an integrator: an expired or unknown code
    /// means "show the interstitial again and let the user read a fresh one", while a consumed code
    /// means the attribution has already been made and asking for another would only produce a
    /// second failure.
    /// </remarks>
    private static IResult ClaimCodeProblem(ClaimCodeStatus status) => status switch
    {
        ClaimCodeStatus.Expired => AttributionProblem.ClaimCodeInvalid(
            "expired",
            "The claim code has passed its time to live. Request a new one from the link page.",
            canReissue: true),

        ClaimCodeStatus.Consumed => AttributionProblem.ClaimCodeInvalid(
            "consumed",
            "The claim code has already been redeemed once and cannot be used again.",
            canReissue: false),

        ClaimCodeStatus.Malformed => AttributionProblem.ClaimCodeInvalid(
            "malformed",
            string.Create(
                CultureInfo.InvariantCulture,
                $"A claim code is {ClaimCode.Length} characters of the alphabet {ClaimCode.Alphabet}."),
            canReissue: true),

        ClaimCodeStatus.Disabled => AttributionProblem.ClaimCodeInvalid(
            "disabled",
            "Claim codes are switched off for this deployment.",
            canReissue: false),

        ClaimCodeStatus.Valid => AttributionProblem.ClaimCodeInvalid(
            "unknown",
            "The claim code could not be redeemed.",
            canReissue: true),

        _ => AttributionProblem.ClaimCodeInvalid(
            "unknown",
            "No such claim code was issued for this tenant, or it has expired.",
            canReissue: true),
    };
}
