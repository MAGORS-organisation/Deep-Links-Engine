using System.Diagnostics.Metrics;

using Dle.Control.Features.Shared;
using Dle.Control.Workers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the background workers (SHARED-KERNEL §15, §B.3).
/// </summary>
/// <remarks>
/// <para>
/// §B.3 puts every background service inside the control plane process and elects a leader through
/// a PostgreSQL advisory lock, "no further system". Every worker registered here therefore derives
/// from <see cref="LeaderElectedBackgroundService"/>, which supplies the lock, the failure
/// isolation and the instruments; a worker cannot opt out of any of the three by being written
/// carelessly.
/// </para>
/// <para>
/// Registering a worker is not the same as running it. Each one reads its own <c>Enabled</c> switch
/// and returns immediately when it is off, so a deployment that splits an API tier from a jobs tier
/// runs the same image twice with different configuration rather than two images.
/// </para>
/// </remarks>
public static class WorkersServiceCollectionExtensions
{
    /// <summary>
    /// Registers the leader election, the worker instruments and the six background workers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:Workers</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The workers depend on services other modules register — <c>IRollupService</c> and
    /// <c>IRetentionService</c> from <c>AddDleAnalytics</c>, <c>WebhookDispatcher</c> from
    /// <c>AddDleWebhooks</c>, <c>IUrlSafetyChecker</c> from <c>AddDleAbuse</c>, the connection pool
    /// from the persistence modules. They are resolved per pass out of a scope rather than injected
    /// as constructor dependencies, so the composition order in <c>Program.cs</c> does not matter
    /// and a worker whose module is absent fails its own pass rather than the whole startup.
    /// </remarks>
    public static IServiceCollection AddDleWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(static provider =>
            new WorkerMetrics(provider.GetService<IMeterFactory>()));

        // The leader lock needs the primary connection pool, which the fast persistence module
        // owns. Registered here as well as by the attribution module so that a jobs-only replica —
        // workers on, SDK endpoints off — still comes up. Every registration in it is TryAdd, so a
        // host that already added it keeps exactly what it had.
        services.AddDleFastPersistence(configuration);

        services.TryAddSingleton<PostgresLeaderLock>();

        // The association files themselves are fetched by DomainVerificationService, which the
        // control core registers along with its SSRF-guarded client. The nightly pass runs exactly
        // the same checks as the operator's verify button on purpose: two implementations would
        // disagree eventually, and the button pressed to reproduce a nightly failure would then
        // show a green tick.

        // The GeoIP source is an explicitly configured provider rather than customer input, so it is
        // not behind the destination guard: a deployment that mirrors the database on its own
        // network must be able to point at it.
        services.AddHttpClient(GeoIpUpdateWorker.HttpClientName, static client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dle-control/1.0 (+https://docs.dle.dev)");
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddHostedService<DomainVerificationWorker>();
        services.AddHostedService<RollupWorker>();
        services.AddHostedService<RetentionWorker>();
        services.AddHostedService<WebhookDispatchWorker>();
        services.AddHostedService<UrlReputationWorker>();
        services.AddHostedService<GeoIpUpdateWorker>();

        return services;
    }
}
