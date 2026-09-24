using System.Diagnostics.Metrics;

using Dle.Control.Features.Attribution;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration of the attribution module (SHARED-KERNEL §15). One call from
/// <c>Dle.Control/Program.cs</c> brings up the SDK facing half of the product.
/// </summary>
/// <remarks>
/// <para>
/// Component C-06 of §B.3 runs inside the control plane process but is a different service from
/// the control plane API: different credentials, different rate limits, different failure
/// behaviour. That separation is expressed here, not documented and hoped for — the SDK endpoints
/// authenticate with their own scheme, carry their own limiters keyed on <c>install_id</c>, and
/// hold the one scope that no configuration policy accepts.
/// </para>
/// <para>
/// The module also brings up <c>Dle.Persistence.Fast</c>. That is deliberate rather than
/// incidental: attribution reads clicks back out of the partitioned click stream through
/// <see cref="Dle.Domain.Ports.IClickLookup"/> and writes SDK events through
/// <see cref="Dle.Domain.Ports.ISdkEventWriter"/>, and both live there because both are Dapper
/// against the same pool the edge uses (ADR-004). The registration is idempotent, so a host that
/// already added it keeps what it had.
/// </para>
/// </remarks>
public static class AttributionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the attribution engine, its storage seam, its instruments and its rate limiters.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:Attribution</c>,
    /// <c>Dle:RateLimits</c> and <c>ConnectionStrings:Postgres</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Options are validated at startup. One of the rules refuses to start at all: the
    /// probabilistic module may not run with consent switched off, because processing device
    /// signals without a legal basis is not a configuration mistake the engine is willing to obey
    /// (see <see cref="AttributionOptionsValidator"/>).
    /// </remarks>
    public static IServiceCollection AddDleAttribution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AttributionOptions>()
            .Bind(configuration.GetSection(AttributionOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<AttributionRateLimitOptions>()
            .Bind(configuration.GetSection(AttributionRateLimitOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AttributionOptions>, AttributionOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AttributionRateLimitOptions>, AttributionOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.TryAddSingleton(static provider =>
            new AttributionMetrics(provider.GetService<IMeterFactory>()));

        // Singleton because a rate limit that resets when a scope ends is not a rate limit. The
        // partitions are keyed on install_id, which arrives in the body, so the leases are taken
        // inside the handlers rather than by middleware (see AttributionRateLimiters).
        services.TryAddSingleton<AttributionRateLimiters>();

        services.TryAddSingleton<LoginKeyHasher>();

        // The click stream reader and the SDK event writer. Idempotent: TryAdd throughout.
        services.AddDleFastPersistence(configuration);

        services.TryAddScoped<IAttributionStore, EfAttributionStore>();
        services.TryAddScoped<ClaimCodeService>();
        services.TryAddScoped<ResolveInstall>();
        services.TryAddScoped<RecordEvents>();

        // Kept registered even though the endpoints authenticate through the SDK key scheme: it is
        // the seam a host uses when it terminates SDK authentication somewhere other than the
        // ASP.NET Core authentication stack, and it is what the security tests exercise directly.
        services.TryAddScoped<SdkKeyAuthenticator>();

        return services;
    }
}
