using System.Diagnostics.Metrics;

using Dle.Persistence.Fast.Attribution;
using Dle.Persistence.Fast.Caching;
using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Data;
using Dle.Persistence.Fast.Links;
using Dle.Persistence.Fast.Telemetry;
using Dle.Persistence.Fast.WellKnown;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the hot-path persistence module (SHARED-KERNEL §15).
/// </summary>
/// <remarks>
/// This is the module's entire public composition surface: one call, everything declarative, nothing
/// for a host to wire by hand. The module has no endpoints of its own, so it has no matching
/// <c>Map…</c> extension.
/// </remarks>
public static class FastPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Dapper and Npgsql hot-path data access, the batched event writers and the two-level
    /// link cache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The application configuration. Read: <c>ConnectionStrings:Postgres</c> (required),
    /// <c>ConnectionStrings:PostgresRead</c> (optional read replica, §B.8 profile B),
    /// <c>ConnectionStrings:Valkey</c> (optional L2 cache), <c>Dle:Edge:Cache</c> and
    /// <c>Dle:Persistence:Fast</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The primary connection string is missing, or an option is malformed or out of range. Validation
    /// happens here rather than through <c>ValidateOnStart</c> because the data annotations validator is
    /// reflective and this assembly stays ahead-of-time friendly (ADR-012); the effect is the same, the
    /// process refuses to start.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Registrations use <c>TryAdd</c> throughout, so a host that wants to substitute a port — a fake
    /// <see cref="ILinkStore"/> in an integration test, for instance — registers it first and this call
    /// leaves it alone.
    /// </para>
    /// <para>
    /// The L2 cache is opt-in by configuration: with <c>ConnectionStrings:Valkey</c> present the shared
    /// level is a Valkey instance reached over the Redis protocol, and without it <c>HybridCache</c>
    /// runs L1-only. That is the difference between §B.8 profile A and profile B, and it is a
    /// configuration value rather than a code path so that both profiles run the same binary.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDleFastPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        FastPersistenceOptions options = FastPersistenceOptions.FromConfiguration(configuration);
        EdgeCacheOptions cacheOptions = EdgeCacheOptions.FromConfiguration(configuration);

        string primaryConnectionString = RequireConnectionString(configuration, FastPersistenceOptions.PrimaryConnectionStringName);
        string? readConnectionString = Trimmed(configuration.GetConnectionString(FastPersistenceOptions.ReadConnectionStringName));
        string? cacheConnectionString = Trimmed(configuration.GetConnectionString(FastPersistenceOptions.CacheConnectionStringName));

        services.TryAddSingleton(options);
        services.TryAddSingleton(cacheOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static sp => new FastPersistenceMetrics(sp.GetService<IMeterFactory>()));

        AddDataSources(services, options, primaryConnectionString, readConnectionString);
        AddStores(services);
        AddEventPipeline(services);
        AddCache(services, cacheOptions, cacheConnectionString);

        return services;
    }

    /// <summary>
    /// Registers the write pool and the read pool.
    /// </summary>
    /// <remarks>
    /// The primary pool carries the long command timeout because a binary <c>COPY</c> has no command
    /// object whose timeout could be raised per call; every read command sets its own short timeout
    /// explicitly, so the pool default never applies to a query on the resolve path.
    /// </remarks>
    private static void AddDataSources(
        IServiceCollection services,
        FastPersistenceOptions options,
        string primaryConnectionString,
        string? readConnectionString)
    {
        services.TryAddSingleton(sp => BuildDataSource(
            sp,
            primaryConnectionString,
            options,
            options.ApplicationName,
            options.WriteCommandTimeoutSeconds));

        services.TryAddSingleton(sp => readConnectionString is null
            ? new DleReadDataSource(sp.GetRequiredService<NpgsqlDataSource>(), isReplica: false)
            : new DleReadDataSource(
                BuildDataSource(
                    sp,
                    readConnectionString,
                    options,
                    string.Concat(options.ApplicationName, "-read"),
                    options.CommandTimeoutSeconds),
                isReplica: true));
    }

    private static void AddStores(IServiceCollection services)
    {
        services.TryAddSingleton<ILinkStore, DapperLinkStore>();
        services.TryAddSingleton<IDomainConfigStore, DapperDomainConfigStore>();
        services.TryAddSingleton<IClickLookup, DapperClickLookup>();
    }

    /// <summary>
    /// Registers the bounded sink, the two <c>COPY</c> writers and the background drain loop.
    /// </summary>
    /// <remarks>
    /// The sink is registered twice on purpose: once by its concrete type, which the drain loop needs
    /// for the channel's read side, and once as <see cref="IClickEventSink"/> resolving to the very same
    /// instance. Two instances would mean the edge writing into a channel nobody reads.
    /// </remarks>
    private static void AddEventPipeline(IServiceCollection services)
    {
        services.TryAddSingleton<ChannelClickEventSink>();
        services.TryAddSingleton<IClickEventSink>(static sp => sp.GetRequiredService<ChannelClickEventSink>());
        services.TryAddSingleton<IClickEventWriter, CopyClickEventWriter>();
        services.TryAddSingleton<ISdkEventWriter, SdkEventBatchWriter>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClickEventBatchWriter>());
    }

    private static void AddCache(IServiceCollection services, EdgeCacheOptions cacheOptions, string? cacheConnectionString)
    {
        IHybridCacheBuilder cache = services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = cacheOptions.DefaultEntryOptions;

            // Tag metrics are what make an invalidation storm visible; without them a mass removal
            // looks identical to a cold cache.
            options.ReportTagMetrics = true;
        });

        _ = cache.AddSerializerFactory<SourceGeneratedHybridCacheSerializerFactory>();

        if (cacheConnectionString is not null)
        {
            // Valkey speaks the Redis protocol, so the StackExchange.Redis backed IDistributedCache is
            // the L2 implementation; HybridCache picks it up simply by it being registered.
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.Configuration = cacheConnectionString;
                redis.InstanceName = "dle:";
            });
        }

        services.TryAddSingleton<ILinkCacheInvalidator, LinkCacheInvalidator>();
    }

    private static NpgsqlDataSource BuildDataSource(
        IServiceProvider serviceProvider,
        string connectionString,
        FastPersistenceOptions options,
        string applicationName,
        int commandTimeoutSeconds)
    {
        // These four settings are owned by Dle:Persistence:Fast rather than by the connection string,
        // so that there is exactly one place that decides them. Anything else in the connection string
        // is left untouched.
        var connection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            MaxPoolSize = options.MaxPoolSize,
            MinPoolSize = options.MinPoolSize,
            CommandTimeout = commandTimeoutSeconds,
            Pooling = true,
        };

        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);

        ILoggerFactory? loggerFactory = serviceProvider.GetService<ILoggerFactory>();

        if (loggerFactory is not null)
        {
            // Parameter logging stays off. A resolve parameter is a host and a slug, and a click lookup
            // parameter is a click identifier; logging them would put user-identifying values into logs
            // at information level, which SHARED-KERNEL §17.5 forbids.
            _ = builder.UseLoggerFactory(loggerFactory);
        }

        return builder.Build();
    }

    private static string RequireConnectionString(IConfiguration configuration, string name)
    {
        string? value = Trimmed(configuration.GetConnectionString(name));

        return value ?? throw new InvalidOperationException(
            $"Connection string '{name}' is required by Dle.Persistence.Fast and was not configured.");
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
