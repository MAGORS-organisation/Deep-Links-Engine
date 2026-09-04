using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the control-plane rate limits (SHARED-KERNEL §15, §E.9, FR-243).
/// </summary>
/// <remarks>
/// <para>
/// The values are the §E.9 table. What is not configurable is that the limits exist: every route
/// names a policy, and the global limiter treats an API route that named none as suspect rather than
/// as unlimited, which is the fail-closed reading of a forgotten attribute (SHARED-KERNEL §17.9).
/// </para>
/// <para>
/// The authentication limit of §E.9 — ten attempts a minute, with constant-time verification either
/// way — is deliberately not here. Endpoint rate limiting runs after authentication, so a limiter
/// registered at this layer could not stop an attacker making the server perform one Argon2id
/// verification per guess, which is both the credential-stuffing surface and, at roughly nineteen
/// mebibytes per verification, a cheap way to exhaust the process. That limit therefore lives inside
/// the authentication handlers, in <see cref="DleAuthAttemptLimiter"/>, and is registered by
/// <c>AddDleIdentity</c>.
/// </para>
/// </remarks>
public static class ControlRateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the rate limiter, its policies and the RFC 9457 rejection response.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:RateLimits</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleControlRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DleRateLimitOptions>()
            .Bind(configuration.GetSection(DleRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;

            AddLinkCreatePolicy(options);
            AddBulkPolicy(options);
            AddFixedRatePolicy(options, DleRateLimitPolicies.Read, static limits => limits.ReadPerMinute);
            AddFixedRatePolicy(options, DleRateLimitPolicies.Write, static limits => limits.WritePerMinute);

            options.GlobalLimiter = BuildSafetyNet();
        });

        return services;
    }

    /// <summary>
    /// Link creation: sixty a minute per API key, ten while the tenant is younger than a week (§E.9).
    /// </summary>
    /// <param name="options">The limiter options being configured.</param>
    /// <remarks>
    /// The tenant's age is part of the partition key, not only of the limiter's configuration. A
    /// partition's options are evaluated once, when it is first created, so a key that started life
    /// in a new tenant would otherwise keep the stricter budget for as long as the partition lived —
    /// and, worse, a key whose partition happened to be created after the seventh day would keep the
    /// looser one. Two partitions, one per tier, makes the transition exact.
    /// </remarks>
    private static void AddLinkCreatePolicy(RateLimiterOptions options)
    {
        options.AddPolicy(DleRateLimitPolicies.LinkCreate, static httpContext =>
        {
            DleRateLimitOptions limits = Limits(httpContext);
            DleCaller? caller = httpContext.User.GetDleCaller();

            bool isNewTenant = IsNewTenant(caller, httpContext, limits);
            int permit = isNewTenant ? limits.NewTenantLinkCreatePerMinute : limits.LinkCreatePerMinute;
            string tier = isNewTenant ? "new" : "established";

            return RateLimitPartition.GetSlidingWindowLimiter(
                string.Create(CultureInfo.InvariantCulture, $"create|{tier}|{PartitionKey(httpContext, caller)}"),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permit,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        });
    }

    /// <summary>Streamed bulk import: two concurrent batches per tenant (§E.9).</summary>
    /// <param name="options">The limiter options being configured.</param>
    /// <remarks>
    /// Concurrency rather than a rate, because the cost of a batch is not one request: it is up to
    /// ten thousand creates, each of which validates a target URL against the network. Bounding how
    /// many run at once is what keeps one tenant's import from becoming everybody's outage.
    /// </remarks>
    private static void AddBulkPolicy(RateLimiterOptions options)
    {
        options.AddPolicy(DleRateLimitPolicies.Bulk, static httpContext =>
        {
            DleRateLimitOptions limits = Limits(httpContext);
            DleCaller? caller = httpContext.User.GetDleCaller();

            string tenant = caller is null
                ? PartitionKey(httpContext, caller)
                : caller.TenantId.ToString();

            return RateLimitPartition.GetConcurrencyLimiter(
                string.Create(CultureInfo.InvariantCulture, $"bulk|{tenant}"),
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = limits.BulkConcurrentBatches,
                    QueueLimit = limits.BulkQueuedBatches,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
        });
    }

    /// <summary>Adds a sliding window policy whose permit count comes from configuration.</summary>
    /// <param name="options">The limiter options being configured.</param>
    /// <param name="policyName">Name of the policy.</param>
    /// <param name="permits">Reads the permit count from the options.</param>
    private static void AddFixedRatePolicy(
        RateLimiterOptions options,
        string policyName,
        Func<DleRateLimitOptions, int> permits)
    {
        options.AddPolicy(policyName, httpContext =>
        {
            DleRateLimitOptions limits = Limits(httpContext);
            DleCaller? caller = httpContext.User.GetDleCaller();

            return RateLimitPartition.GetSlidingWindowLimiter(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{policyName}|{PartitionKey(httpContext, caller)}"),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permits(limits),
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        });
    }

    /// <summary>
    /// The limiter that catches an API route which named no policy.
    /// </summary>
    /// <returns>The global limiter.</returns>
    /// <remarks>
    /// A route that carries a named policy passes through untouched, so nothing is counted twice. A
    /// route under <c>/api</c> or <c>/v1</c> that carries none is given the read budget rather than
    /// being left unlimited: forgetting the attribute should degrade to something conservative, not
    /// to nothing. Static files and the health probes are excluded, because throttling a liveness
    /// probe would turn a busy minute into a restart.
    /// </remarks>
    private static PartitionedRateLimiter<HttpContext> BuildSafetyNet() =>
        PartitionedRateLimiter.Create<HttpContext, string>(static httpContext =>
        {
            if (httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null)
            {
                return RateLimitPartition.GetNoLimiter("named-policy");
            }

            PathString path = httpContext.Request.Path;

            bool isApi = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase);

            if (!isApi)
            {
                return RateLimitPartition.GetNoLimiter("not-api");
            }

            DleRateLimitOptions limits = Limits(httpContext);
            DleCaller? caller = httpContext.User.GetDleCaller();

            return RateLimitPartition.GetSlidingWindowLimiter(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"unclassified|{PartitionKey(httpContext, caller)}"),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.ReadPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        });

    /// <summary>Answers a refused request with an RFC 9457 document and a <c>Retry-After</c>.</summary>
    /// <param name="context">The rejected request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <remarks>
    /// §E.9 answers a rejected request with 429 and a <c>Retry-After</c>, never with the framework's
    /// default 503, which tells a caller to try again immediately and turns a limit into a
    /// self-inflicted retry storm.
    /// </remarks>
    private static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        DleRateLimitOptions limits = Limits(context.HttpContext);

        int retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan window)
            ? Math.Max(1, (int)Math.Ceiling(window.TotalSeconds))
            : limits.RetryAfterSeconds;

        return new ValueTask(DleProblem
            .RateLimited(
                retryAfter,
                "The request was refused by a rate limit. The Retry-After header says when to try "
                + "again; the limits are configurable per deployment and are documented in §E.9.")
            .ExecuteAsync(context.HttpContext));
    }

    /// <summary>Reads the limit configuration for a request.</summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>The current limits.</returns>
    private static DleRateLimitOptions Limits(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IOptionsMonitor<DleRateLimitOptions>>().CurrentValue;

    /// <summary>Whether the caller's tenant is still inside the stricter new-tenant window (§E.9).</summary>
    /// <param name="caller">The authenticated caller, when there is one.</param>
    /// <param name="httpContext">The request, for the clock.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns><see langword="true"/> when the stricter budget applies.</returns>
    /// <remarks>
    /// An unauthenticated request counts as new. A freshly provisioned tenant creating links as fast
    /// as the API allows is the shape of a throwaway account mass-producing phishing short URLs, and
    /// the conservative branch is the correct default when the engine cannot tell.
    /// </remarks>
    private static bool IsNewTenant(
        DleCaller? caller,
        HttpContext httpContext,
        DleRateLimitOptions limits)
    {
        if (caller?.TenantCreatedAt is not DateTimeOffset createdAt)
        {
            return true;
        }

        TimeProvider clock = httpContext.RequestServices.GetRequiredService<TimeProvider>();

        return clock.GetUtcNow() - createdAt < TimeSpan.FromDays(limits.NewTenantDays);
    }

    /// <summary>
    /// Builds the partition key for one caller.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="caller">The authenticated caller, when there is one.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// The non-secret key prefix identifies an authenticated caller, which is what §E.9 keys the link
    /// creation limit on. An unauthenticated request falls back to the remote address, reduced to a
    /// /64 for IPv6 because a single residential connection is routinely handed a whole /64 and
    /// keying on the exact address would hand one household an unlimited quota. The key never reaches
    /// a log (SHARED-KERNEL §17.5).
    /// </remarks>
    private static string PartitionKey(HttpContext httpContext, DleCaller? caller)
    {
        if (caller is not null)
        {
            return caller.KeyPrefix
                ?? caller.ActorId?.ToString()
                ?? caller.TenantId.ToString();
        }

        IPAddress? address = httpContext.Connection.RemoteIpAddress;

        if (address is null)
        {
            return "anonymous";
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written) || written != 16)
        {
            return address.ToString();
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(bytes[..8])}::/64");
    }
}
