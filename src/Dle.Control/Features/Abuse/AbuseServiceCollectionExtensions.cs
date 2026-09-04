using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

using Dle.Control.Features.Abuse;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the abuse module (SHARED-KERNEL §15).
/// </summary>
/// <remarks>
/// One call from <c>Dle.Control/Program.cs</c> brings up the target URL safety pipeline, the public
/// notice and action form's rate limit, and the enforcement service behind quarantine. The nightly
/// re-check that uses all three is a worker and is registered by <c>AddDleWorkers</c>.
/// </remarks>
public static class AbuseServiceCollectionExtensions
{
    /// <summary>
    /// Name of the rate limiter policy applied to the public abuse form (§E.9: five per hour per
    /// IP address).
    /// </summary>
    public const string ReportRateLimitPolicy = "dle-abuse-report";

    /// <summary>
    /// Registers the abuse module: options, the URL safety checker and its reputation providers,
    /// the quarantine service and the rate limit of the public form.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:Abuse</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The reputation providers are registered additively and consulted in order, local blocklist
    /// first. That order is a policy statement: the operator's own list beats any feed, because it
    /// is how a deployment answers a complaint in the minute it arrives.
    /// </para>
    /// <para>
    /// No resilience handler is attached to the reputation client. A retrying handler would turn a
    /// slow feed into a slow link creation, and the pipeline already treats an unreachable source
    /// as "no opinion" rather than as approval — the safe answer, reached faster.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDleAbuse(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AbuseOptions>()
            .Bind(configuration.GetSection(AbuseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AbuseMetrics>();
        services.TryAddSingleton<ReporterEmailHasher>();

        services.AddHttpClient(UrlHausReputationProvider.HttpClientName, static client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dle-control/1.0 (+https://docs.dle.dev)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // Order matters and is expressed by registration order: the operator's list, then the feed.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IUrlReputationProvider, BlocklistReputationProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IUrlReputationProvider, UrlHausReputationProvider>());

        services.TryAddSingleton<IUrlSafetyChecker, DefaultUrlSafetyChecker>();

        services.TryAddScoped<AbuseLinkLocator>();
        services.TryAddScoped<LinkQuarantineService>();

        AddReportRateLimit(services);

        return services;
    }

    /// <summary>Adds the fixed window limiter the public form runs behind.</summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// <para>
    /// A fixed window keyed by the caller's address, with the value from §E.9. Fixed rather than
    /// sliding because the limit exists to stop a flood, not to smooth a legitimate burst: nobody
    /// files five genuine abuse reports a minute apart.
    /// </para>
    /// <para>
    /// <c>AddRateLimiter</c> configures options additively, so calling it here composes with the
    /// control plane's own limits rather than replacing them. Whether the limit is enforced still
    /// depends on the host running the rate limiting middleware, which is the composition root's
    /// job.
    /// </para>
    /// </remarks>
    private static void AddReportRateLimit(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // §E.9 answers a rejected request with 429 and a Retry-After, never with 503, which is
            // the framework default and would tell a caller to try again immediately.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(ReportRateLimitPolicy, static httpContext =>
            {
                AbuseOptions abuse = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<AbuseOptions>>()
                    .CurrentValue;

                string key = PartitionKey(httpContext.Connection.RemoteIpAddress);

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = abuse.ReportsPerHourPerIp,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });
        });
    }

    /// <summary>
    /// Builds the partition key for one caller.
    /// </summary>
    /// <param name="address">The remote address, or <see langword="null"/>.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// IPv6 callers are partitioned by their /64 prefix rather than by the exact address: a single
    /// residential connection is routinely handed a whole /64, so keying on the full address would
    /// hand one household an unlimited quota. A request with no address at all — a unix socket, a
    /// misconfigured proxy — shares one bucket, which fails towards the limit rather than around
    /// it. The key never reaches a log (SHARED-KERNEL §17.5).
    /// </remarks>
    private static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
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

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Convert.ToHexStringLower(bytes[..8])}::/64");
    }
}
