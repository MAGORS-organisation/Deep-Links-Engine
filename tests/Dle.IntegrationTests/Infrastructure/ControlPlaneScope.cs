using Dle.Persistence;
using Dle.Persistence.Tenancy;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The control plane's persistence stack — <see cref="DleDbContext"/> and its repositories — over
/// one test database, inside one tenant.
/// </summary>
/// <remarks>
/// <para>
/// Composed through <see cref="DlePersistenceServiceCollectionExtensions.AddDlePersistence"/>, so
/// the model, the named query filters and the repository registrations are the ones a deployment
/// gets. A test that hand-built a <see cref="DleDbContext"/> would be testing a context nobody runs.
/// </para>
/// <para>
/// The tenant scope is the whole of isolation. <see cref="ITenantContext"/> supplies the value the
/// named tenant filter reads, so entering a scope here is exactly what the authentication middleware
/// does per request — which is why a repository call made in tenant A's scope cannot see tenant B's
/// rows without anyone writing a comparison (FR-241, T-09, TC-166).
/// </para>
/// </remarks>
public sealed class ControlPlaneScope : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly IDisposable? _tenantScope;

    private ControlPlaneScope(ServiceProvider provider, AsyncServiceScope scope, IDisposable? tenantScope)
    {
        _provider = provider;
        _scope = scope;
        _tenantScope = tenantScope;
    }

    /// <summary>The context, already inside the tenant scope.</summary>
    public DleDbContext Db => _scope.ServiceProvider.GetRequiredService<DleDbContext>();

    /// <summary>Resolves a repository or any other registered service.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The service.</returns>
    public T Resolve<T>()
        where T : notnull =>
        _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Opens the persistence stack over a test database, optionally inside a tenant.
    /// </summary>
    /// <param name="database">The test database.</param>
    /// <param name="tenantId">
    /// The tenant to enter. Omitted, no tenant is established at all — which is how a query that
    /// forgot the tenant is made to fail loudly instead of returning everybody's rows.
    /// </param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is <see langword="null"/>.</exception>
    public static ControlPlaneScope Open(TestDatabase database, Guid? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Postgres"] = database.ConnectionString,
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDlePersistence(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        AsyncServiceScope scope = provider.CreateAsyncScope();

        IDisposable? tenantScope = tenantId is Guid id
            ? scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginScope(id)
            : null;

        return new ControlPlaneScope(provider, scope, tenantScope);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _tenantScope?.Dispose();

        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
