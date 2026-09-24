using System.Globalization;
using System.Threading.RateLimiting;

using Dle.Edge.RateLimiting;
using Dle.Edge.Rendering;
using Dle.Edge.Resolution;
using Dle.Edge.Telemetry;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the edge rate limiting module
/// (SHARED-KERNEL §15, §E.9, T-07).
/// </summary>
/// <remarks>
/// <para>
/// Two of the three §E.9 rows for the edge are installed here as one global partitioned limiter: the
/// sliding window over <c>GET /{slug}</c> and the tighter sliding window over <c>GET /{slug}/qr</c>.
/// The third — the token bucket over responses that ended in 404, and the shadow ban behind it —
/// cannot live in middleware at all, because middleware decides before the handler runs and a limit on
/// 404s is a limit on the <em>outcome</em>; it is <see cref="NotFoundEnumerationGuard"/>, registered
/// here and charged by the resolve pipeline.
/// </para>
/// <para>
/// All three are separate partitions and that separation is the property that matters most in the
/// table. If the anti-enumeration budget shared a counter with successful resolves, a campaign going
/// viral would exhaust it and switch the defence off at the moment the service is most visible and
/// most worth scanning (§E.9, T-07).
/// </para>
/// <para>
/// One global limiter rather than per-endpoint policies, because the QR endpoint is mapped by the
/// rendering module: a limit that only applies when another module remembers to opt in is a limit that
/// is one merge away from not existing.
/// </para>
/// </remarks>
public static class EdgeRateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the §E.9 limiters and the enumeration guard.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration; reads <c>Dle:RateLimits:Edge</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleEdgeRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EdgeRateLimitOptions>()
            .Bind(configuration.GetSection(EdgeRateLimitOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<EdgeRateLimitOptions>, EdgeRateLimitOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NotFoundEnumerationGuard>();

        EdgeRateLimitOptions options = configuration.GetSection(EdgeRateLimitOptions.SectionName)
            .Get<EdgeRateLimitOptions>() ?? new EdgeRateLimitOptions();

        if (!options.Enabled)
        {
            // Still register the middleware's services so that the pipeline shape does not depend on
            // configuration; the limiter simply admits everything.
            services.AddRateLimiter(static limiter =>
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    static _ => RateLimitPartition.GetNoLimiter(EdgeRateLimitPartitions.UnlimitedKey)));

            return services;
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = BuildGlobalLimiter(options);
            limiter.OnRejected = RejectAsync;
        });

        return services;
    }

    /// <summary>
    /// Builds the partitioned limiter covering the two request-shaped rows of §E.9.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> BuildGlobalLimiter(EdgeRateLimitOptions options)
    {
        ResolveRateLimitOptions resolve = options.Resolve;
        QrRateLimitOptions qr = options.Qr;

        return PartitionedRateLimiter.Create<HttpContext, EdgeRateLimitPartition>(context =>
        {
            EdgeRateLimitKind kind = EdgeRateLimitPartitions.Classify(context.Request.Method, context.Request.Path);

            switch (kind)
            {
                case EdgeRateLimitKind.Resolve:
                {
                    IIpHasher hasher = context.RequestServices.GetRequiredService<IIpHasher>();
                    string key = EdgeRateLimitPartitions.NetworkKey(hasher, context.Connection.RemoteIpAddress);

                    return RateLimitPartition.GetSlidingWindowLimiter(
                        new EdgeRateLimitPartition(EdgeRateLimitKind.Resolve, key),
                        _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = resolve.PermitsPerWindow,
                            Window = TimeSpan.FromSeconds(resolve.WindowSeconds),
                            SegmentsPerWindow = resolve.SegmentsPerWindow,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        });
                }

                case EdgeRateLimitKind.Qr:
                {
                    string key = EdgeRateLimitPartitions.AddressKey(context.Connection.RemoteIpAddress);

                    return RateLimitPartition.GetSlidingWindowLimiter(
                        new EdgeRateLimitPartition(EdgeRateLimitKind.Qr, key),
                        _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = qr.PermitsPerWindow,
                            Window = TimeSpan.FromSeconds(qr.WindowSeconds),
                            SegmentsPerWindow = qr.SegmentsPerWindow,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        });
                }

                case EdgeRateLimitKind.Unlimited:
                default:
                    // Health probes, association files and static assets. A probe a rate limiter can
                    // answer with 429 is a probe that pulls an instance out of rotation during exactly
                    // the traffic spike it exists to survive.
                    return RateLimitPartition.GetNoLimiter(
                        new EdgeRateLimitPartition(EdgeRateLimitKind.Unlimited, EdgeRateLimitPartitions.UnlimitedKey));
            }
        });
    }

    /// <summary>
    /// Writes the 429, always with <c>Retry-After</c> (§E.9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 429 without <c>Retry-After</c> tells a client to back off and refuses to say how far, which in
    /// practice means it retries immediately and the limiter spends the rest of the window rejecting
    /// the same client. When the limiter reports a wait it is used verbatim; when it does not, the
    /// configured window is the honest upper bound.
    /// </para>
    /// <para>
    /// A browser gets the same accessible page as every other error, and a machine gets an RFC 9457
    /// problem document. The distinction is made on <c>Accept</c> alone, because it is the only
    /// statement the client has actually made about what it can read.
    /// </para>
    /// </remarks>
    private static ValueTask RejectAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        HttpContext context = rejected.HttpContext;

        TimeSpan? retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait)
            ? wait
            : null;

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (retryAfter is { } value)
        {
            context.Response.Headers.RetryAfter =
                Math.Max(1, (long)Math.Ceiling(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        // Zero seconds is the honest value, not a placeholder: the limiter rejects before the resolver
        // runs, so nothing was done. The sample is tagged outcome=rate_limited, which is what lets an
        // NFR-01 dashboard exclude it from the latency objective rather than have it drag the median
        // down during an attack.
        context.RequestServices.GetRequiredService<EdgeMetrics>().RecordResolve(
            0d,
            ResolveOutcomes.RateLimited,
            CacheLevels.None,
            ChannelNames.From(ClientChannel.Unknown),
            DecisionNames.NotFound,
            Platform.Unknown,
            isBot: false);

        IResult result = PrefersHtml(context.Request)
            ? InterstitialResults.TooManyRequests(AcceptedLanguage(context.Request), domain: null, retryAfter)
            : Results.Problem(
                title: "Too many requests.",
                statusCode: StatusCodes.Status429TooManyRequests,
                type: ProblemCodes.RateLimited);

        return new ValueTask(result.ExecuteAsync(context));
    }

    /// <summary>
    /// The primary language subtag of <c>Accept-Language</c>, so the 429 page is in the visitor's
    /// language even though no client classification has happened at this point in the pipeline.
    /// </summary>
    private static string? AcceptedLanguage(HttpRequest request)
    {
        string? header = request.Headers.AcceptLanguage.Count == 0 ? null : request.Headers.AcceptLanguage[0];

        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        ReadOnlySpan<char> span = header.AsSpan();
        int end = span.IndexOfAny(',', ';');

        if (end >= 0)
        {
            span = span[..end];
        }

        span = span.Trim();

        int dash = span.IndexOf('-');

        if (dash >= 0)
        {
            span = span[..dash];
        }

        return span.Length is > 0 and <= 8 ? span.ToString() : null;
    }

    private static bool PrefersHtml(HttpRequest request)
    {
        foreach (string? accept in request.Headers.Accept)
        {
            if (accept is not null && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
