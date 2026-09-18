using System.Diagnostics.Metrics;
using System.Net.Http;

using Dle.Control.Features.Jwks;
using Dle.Control.Features.Shared;
using Dle.Control.Features.Webhooks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the webhook module (SHARED-KERNEL §15). Component C-10 of §B.3.
/// </summary>
/// <remarks>
/// <para>
/// The interesting registration is the HTTP client. Its primary handler is
/// <see cref="PublicEndpointGuard"/>, which resolves the destination and validates the address
/// inside the connect callback — the only place where the address that is checked is provably the
/// address the socket connects to. Redirects are off, cookies are off, proxies are off. A webhook
/// destination is a URL a customer supplies and this process then fetches from inside its own
/// network, so it is a server side request forgery primitive unless something makes it not one
/// (T-02).
/// </para>
/// <para>
/// No resilience handler is attached. Retries are the dispatcher's business, with a schedule, a
/// bound, jitter and a dead letter queue that an operator can see; a transparent retry handler
/// underneath would multiply those attempts invisibly and land three requests on a struggling
/// endpoint where the schedule says one.
/// </para>
/// <para>
/// <see cref="WebhookSigningKeys"/> is registered once and reached twice: as itself, by the
/// dispatcher that signs, and as an <see cref="IJwksContributor"/>, by the endpoint that publishes.
/// One instance, so the key a receiver fetches is the key a delivery was signed with.
/// </para>
/// </remarks>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the webhook signing keys, the secret protector, the dispatcher and its client.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:Webhooks</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleWebhooks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(static provider =>
            new WebhookMetrics(provider.GetService<IMeterFactory>()));

        services.TryAddSingleton<WebhookSecretProtector>();
        services.TryAddSingleton<WebhookSigningKeys>();

        // The same instance the dispatcher signs with is the instance the JWKS endpoint publishes.
        services.AddSingleton<IJwksContributor>(
            static provider => provider.GetRequiredService<WebhookSigningKeys>());

        services.AddHttpClient(WebhookDispatcher.HttpClientName, static client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dle-control/1.0 (+https://docs.dle.dev)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            // The per-attempt timeout is applied by the dispatcher from options; this is only a
            // ceiling so a misconfigured option cannot hold a worker thread indefinitely.
            client.Timeout = TimeSpan.FromMinutes(2);
        })
        .ConfigurePrimaryHttpMessageHandler(static provider =>
        {
            WebhookOptions options = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;

            return PublicEndpointGuard.CreateHandler(
                TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
                options.AllowPrivateDestinations);
        });

        services.TryAddSingleton<WebhookDispatcher>();
        services.TryAddScoped<WebhookDeliveryStore>();

        return services;
    }
}
