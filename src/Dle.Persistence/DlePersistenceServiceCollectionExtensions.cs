using Dle.Domain.Ports;
using Dle.Persistence;
using Dle.Persistence.Repositories;

using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the control-plane persistence module (SHARED-KERNEL §15).
/// </summary>
public static class DlePersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds the control-plane <see cref="DleDbContext"/>, the tenant context and every repository
    /// the control plane needs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration carrying <c>ConnectionStrings:Postgres</c> and the
    /// optional <c>Dle:Persistence</c> section.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings:Postgres</c> is missing or blank.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The connection string is validated here rather than on first use. A control plane that
    /// starts and then fails every request is worse than one that refuses to start, and the failure
    /// is a configuration mistake that a deployment wants to see immediately.
    /// </para>
    /// <para>
    /// The context is registered with <c>AddDbContext</c> and not with the pooling variant on
    /// purpose: a pooled context may only take <c>DbContextOptions</c> in its constructor, and this
    /// one also takes the tenant context and the clock. Pooling buys throughput on the resolve
    /// path, which does not use EF Core at all (ADR-004), so there is nothing to trade away here.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDlePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string? connectionString =
            configuration.GetConnectionString(DlePersistenceOptions.ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The control plane needs a PostgreSQL connection string in " +
                "ConnectionStrings:Postgres (§C.4). Set it before calling AddDlePersistence.");
        }

        services.AddOptions<DlePersistenceOptions>()
            .Bind(configuration.GetSection(DlePersistenceOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITenantContext, AmbientTenantContext>();

        services.AddDbContext<DleDbContext>((provider, builder) =>
        {
            DlePersistenceOptions options =
                provider.GetRequiredService<IOptions<DlePersistenceOptions>>().Value;

            builder.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(DleDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable(options.MigrationsHistoryTable);
                npgsql.CommandTimeout(options.CommandTimeoutSeconds);

                if (options.MaxRetryCount > 0)
                {
                    npgsql.EnableRetryOnFailure(
                        options.MaxRetryCount,
                        TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                        errorCodesToAdd: null);
                }
            });

            // Every relationship in this model is configured without navigation properties and
            // between entity types that carry the tenant filter. EF Core cannot tell that the
            // filters agree by construction and warns that a required principal might be filtered
            // out; here it never is, because both ends are scoped to the same tenant.
            builder.ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        });

        services.TryAddScoped<TenantRepository>();
        services.TryAddScoped<DomainRepository>();
        services.TryAddScoped<AppRepository>();
        services.TryAddScoped<LinkRepository>();
        services.TryAddScoped<ApiKeyRepository>();
        services.TryAddScoped<AttributionRepository>();
        services.TryAddScoped<AbuseRepository>();
        services.TryAddScoped<WebhookRepository>();
        services.TryAddScoped<AuditLogWriter>();
        services.TryAddScoped<IdempotencyStore>();

        // The outbox is a persistence concern: enqueueing a delivery is a row, and it has to join
        // the transaction that produced the event. TryAdd, so a deployment that ships a different
        // transport can register its own before calling this.
        services.TryAddScoped<IWebhookOutbox>(
            provider => provider.GetRequiredService<WebhookRepository>());

        // Singleton: it holds a claimed block of counter values in memory and hands them out
        // without a round trip (ADR-007). It opens its own scopes for the rare refill.
        services.TryAddSingleton<SlugSequenceAllocator>();

        return services;
    }
}
