using System.Net;

using Dle.Edge.Clients;
using Dle.Edge.Configuration;
using Dle.Edge.Health;
using Dle.Edge.Rendering;
using Dle.Edge.Resolution;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the edge data plane (SHARED-KERNEL §15).
/// </summary>
/// <remarks>
/// <para>
/// Everything the resolve path needs and nothing it does not: the options that §16 defines, the client
/// classification chain, the routing engine, the page renderer and the pipeline that joins them. The
/// cache, the link store, the event sink, the click identifier codec and the instruments come from
/// <c>AddDleFastPersistence</c>, <c>AddDleCrypto</c> and <c>AddDleTelemetry</c>, which is why
/// <c>Program.cs</c> calls exactly five <c>Add…</c> methods and configures nothing by hand.
/// </para>
/// <para>
/// Every seam is registered with <c>TryAdd</c>. A test host, or a deployment that substitutes a port —
/// its own <see cref="IGeoIpResolver"/>, <see cref="IBotVerifier"/> or <see cref="IClientClassifier"/> —
/// registers it before this call and keeps it.
/// </para>
/// </remarks>
public static class EdgeCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the resolve pipeline and binds every <c>Dle:Edge</c> option of §16.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The application configuration. Read: <c>Dle:Edge</c>, <c>Dle:Edge:Interstitial</c>,
    /// <c>Dle:Edge:BotDetection</c>, <c>Dle:Edge:GeoIp</c>, <c>Dle:Edge:Security</c>,
    /// <c>Dle:Edge:Network</c> and <c>Dle:Privacy</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleEdgeCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services, configuration);

        services.TryAddSingleton(TimeProvider.System);

        // RFC 9457 problem documents for the two machine-readable failures the edge can produce: an
        // unavailable dependency and a refused request. Everything a person sees is an HTML page.
        services.AddProblemDetails();

        AddClientPipeline(services, configuration);

        services.TryAddSingleton<IRoutingEngine, RoutingEngine>();
        services.TryAddSingleton<LinkResolver>();

        services.AddHealthChecks()
            .AddCheck<ResolvePipelineHealthCheck>(
                ResolvePipelineHealthCheck.CheckName,
                failureStatus: HealthStatus.Degraded,
                tags: [ResolvePipelineHealthCheck.ReadyTag])
            .AddCheck<DatabaseReachabilityHealthCheck>(
                DatabaseReachabilityHealthCheck.CheckName,
                failureStatus: HealthStatus.Degraded,
                tags: [ResolvePipelineHealthCheck.ReadyTag]);

        ConfigureForwardedHeaders(services, configuration);

        // The server banner is free reconnaissance and the edge has no reason to publish its version
        // to every scanner on the internet.
        services.Configure<KestrelServerOptions>(static kestrel => kestrel.AddServerHeader = false);

        return services;
    }

    /// <summary>
    /// Binds each §16 subsection as its own options type.
    /// </summary>
    /// <remarks>
    /// Separately rather than as nested properties of one object, because
    /// <c>ValidateDataAnnotations</c> does not recurse into a nested instance: an
    /// <c>AutoRedirectMs</c> of zero nested inside a parent would pass validation and fail in
    /// production instead. One registration per section also means the start-up diagnostic names the
    /// section the bad value came from.
    /// </remarks>
    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        Bind<EdgeOptions>(services, configuration, EdgeOptions.SectionName);

        // Fully qualified because two types named InterstitialOptions currently bind to the same
        // section: this one, which every rendered page reads through HtmlPageResult, and an older
        // string-typed twin in Dle.Edge.Configuration that nothing consumes. Binding the one the
        // pages actually use is the choice that cannot be wrong; the duplicate belongs to the
        // rendering and configuration owners to remove.
        Bind<Dle.Edge.Rendering.InterstitialOptions>(
            services,
            configuration,
            Dle.Edge.Rendering.InterstitialOptions.SectionName);

        Bind<BotDetectionOptions>(services, configuration, BotDetectionOptions.SectionName);
        Bind<GeoIpOptions>(services, configuration, GeoIpOptions.SectionName);
        Bind<SecurityHeaderOptions>(services, configuration, SecurityHeaderOptions.SectionName);
        Bind<NetworkOptions>(services, configuration, NetworkOptions.SectionName);
        Bind<EdgePrivacyOptions>(services, configuration, EdgePrivacyOptions.SectionName);
    }

    private static void Bind<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers client classification: geography, crawler confirmation, and the classifier itself.
    /// </summary>
    /// <remarks>
    /// The geographic provider is chosen once, here, from configuration rather than per request. A
    /// deployment that ships no GeoLite2 file gets <see cref="NullGeoIpResolver"/> and the background
    /// refresher is not started at all, so the difference between "no database configured" and "a
    /// database that is being watched" costs nothing at run time.
    /// </remarks>
    private static void AddClientPipeline(IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration[$"{GeoIpOptions.SectionName}:{nameof(GeoIpOptions.Provider)}"]
            ?? MaxMindGeoIpResolver.ProviderName;

        if (string.Equals(provider.Trim(), MaxMindGeoIpResolver.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<MaxMindGeoIpResolver>();
            services.TryAddSingleton<IGeoIpResolver>(static sp => sp.GetRequiredService<MaxMindGeoIpResolver>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GeoIpDatabaseRefresher>());
        }
        else
        {
            services.TryAddSingleton<IGeoIpResolver>(NullGeoIpResolver.Instance);
        }

        services.TryAddSingleton<IBotVerifier, BotVerifier>();
        services.TryAddSingleton<IClientClassifier, ClientClassifier>();
    }

    /// <summary>
    /// Translates <c>Dle:Edge:Network</c> into the framework's forwarded header options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client address is not cosmetic here: it is the rate limiting partition key (§E.9), the input
    /// to the hashed identifier in the click stream (FR-247) and the geographic lookup key. Honouring
    /// <c>X-Forwarded-For</c> from an arbitrary peer would let any client choose its own rate limit
    /// bucket and its own country, so the header is only trusted when the immediate peer is a
    /// configured proxy — which is why the framework's default of "no known proxies" is cleared here
    /// rather than left in place.
    /// </para>
    /// <para>
    /// An entry that does not parse is dropped rather than silently widening trust. The default
    /// loopback entries are removed for the same reason: a deployment that names its proxies means
    /// those proxies.
    /// </para>
    /// </remarks>
    private static void ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        NetworkOptions network = configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>()
            ?? new NetworkOptions();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = network.ForwardLimit;

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (string candidate in network.KnownProxies)
            {
                if (IPAddress.TryParse(candidate, out IPAddress? address))
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (string candidate in network.KnownNetworks)
            {
                if (System.Net.IPNetwork.TryParse(candidate, out System.Net.IPNetwork parsed))
                {
                    options.KnownIPNetworks.Add(parsed);
                }
            }
        });
    }
}
