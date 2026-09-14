using Dle.Analytics.ClickHouse;
using Dle.Analytics.Postgres;
using Dle.Domain.Ports;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the analytics module (SHARED-KERNEL §15). One extension, one call from
/// <c>Dle.Control/Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam ADR-006 exists for. <c>Dle:Analytics:Provider</c> chooses between the
/// partitioned PostgreSQL tables a self-hoster already has and a ClickHouse cluster an
/// installation past roughly fifty million events a month can move to. Both satisfy
/// <see cref="IClickAnalyticsStore"/> identically, so the decision stays configuration.
/// </para>
/// <para>
/// The extension lives in the PostgreSQL project because PostgreSQL is the default: the opt-in
/// provider is reached from the default, never the other way round. Selecting the ClickHouse
/// provider requires a compile-time reference to its assembly, which is why the reference exists;
/// what it does not require is any ClickHouse type being touched when the provider is not
/// selected, and none is. On the default provider nothing in that assembly is loaded, no
/// connection is attempted, and no background service is started.
/// </para>
/// <para>
/// Nothing here performs I/O. Connection factories build their pools lazily, so a database that is
/// not reachable yet produces a failed query later rather than a failed startup — with one
/// deliberate exception: a missing connection string for the selected provider is a configuration
/// error and fails immediately, because it can only ever fail.
/// </para>
/// </remarks>
public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the analytics store, the rollup and retention services and the report exporters.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for
    /// <c>Dle:Analytics</c>, <c>Dle:Privacy:Retention</c> and the <c>Postgres</c> or
    /// <c>ClickHouse</c> connection string.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The connection string required by the selected
    /// provider is missing.</exception>
    public static IServiceCollection AddDleAnalytics(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<AnalyticsOptions>()
            .Bind(configuration.GetSection(AnalyticsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => AnalyticsProviderNames.IsKnown(options.Provider),
                "Dle:Analytics:Provider must be 'postgres' or 'clickhouse'.")
            .ValidateOnStart();

        services.AddOptions<AnalyticsRetentionOptions>()
            .Bind(configuration.GetSection(AnalyticsRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Exporters are provider independent: they format an already materialised report, so the
        // store decides what a tenant may see and an exporter is never a second way to ask.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAnalyticsExporter, CsvAnalyticsExporter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAnalyticsExporter, ParquetAnalyticsExporter>());

        string provider = AnalyticsProviderNames.Normalize(
            configuration[AnalyticsOptions.SectionName + ":Provider"]);

        if (string.Equals(provider, AnalyticsProviderNames.ClickHouse, StringComparison.Ordinal))
        {
            AddClickHouseProvider(services, configuration);
        }
        else
        {
            AddPostgresProvider(services, configuration);
        }

        return services;
    }

    private static void AddPostgresProvider(IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = RequireConnectionString(configuration, "Postgres");

        services.TryAddSingleton<IAnalyticsConnectionFactory>(serviceProvider =>
            new NpgsqlAnalyticsConnectionFactory(
                connectionString,
                serviceProvider.GetRequiredService<IOptionsMonitor<AnalyticsOptions>>()));

        services.TryAddSingleton<IClickAnalyticsStore, PostgresClickAnalyticsStore>();
        services.TryAddSingleton<IRollupService, PostgresRollupService>();
        services.TryAddSingleton<IRetentionService, PostgresRetentionService>();
    }

    private static void AddClickHouseProvider(
        IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = RequireConnectionString(configuration, "ClickHouse");

        services.AddOptions<ClickHouseAnalyticsOptions>()
            .Bind(configuration.GetSection(ClickHouseAnalyticsOptions.SectionName))
            .PostConfigure<IOptionsMonitor<AnalyticsRetentionOptions>>((options, retention) =>
            {
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    options.ConnectionString = connectionString;
                }

                // The TTL in the embedded DDL is generated from this value, so the two providers
                // expire raw events on the same schedule (FR-247).
                options.RawRetentionDays = retention.CurrentValue.RawDays;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IClickHouseConnectionFactory, ClickHouseConnectionFactory>();
        services.TryAddSingleton<IClickAnalyticsStore, ClickHouseClickAnalyticsStore>();

        // Registered additively rather than with TryAdd: a host that resolves the single service
        // gets ClickHouse, and a host that resolves IEnumerable<IClickEventWriter> gets every
        // registered writer, which is how a dual write during a migration is expressed.
        services.TryAddSingleton<ClickHouseEventSink>();
        services.AddSingleton<IClickEventWriter>(
            static serviceProvider => serviceProvider.GetRequiredService<ClickHouseEventSink>());
        services.AddSingleton<ISdkEventWriter>(
            static serviceProvider => serviceProvider.GetRequiredService<ClickHouseEventSink>());

        // ClickHouse aggregates on read and expires raw events through the table TTL, so the two
        // maintenance ports are still registered but report that there is nothing to schedule.
        services.TryAddSingleton<IRollupService, ProviderManagedRollupService>();
        services.TryAddSingleton<IRetentionService, ProviderManagedRetentionService>();
    }

    private static string RequireConnectionString(IConfiguration configuration, string name)
    {
        string? value = configuration.GetConnectionString(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Connection string '{name}' is required by the configured analytics provider "
                + "(Dle:Analytics:Provider). Set ConnectionStrings:" + name + ".");
        }

        return value;
    }
}
