using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.Control.Features.Bootstrap;

/// <summary>
/// Registers the control plane bootstrap options and hosted service.
/// </summary>
public static class BootstrapServiceCollectionExtensions
{
    /// <summary>
    /// Adds bootstrap initialization services for initial tenant and API key provisioning.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDleBootstrap(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BootstrapOptions>()
            .Bind(configuration.GetSection(BootstrapOptions.SectionName));

        services.AddHostedService<ControlBootstrapService>();

        return services;
    }
}
