using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// A real host, composed by its own <c>Program</c>, wired to the test database and cache.
/// </summary>
/// <typeparam name="TEntryPoint">Any public type from the host's assembly.</typeparam>
/// <remarks>
/// <para>
/// The entry point type is a plain options class rather than <c>Program</c>. Both hosts declare a
/// <c>public partial class Program</c> in the global namespace, so a test assembly that references
/// both cannot name either without an extern alias;
/// <see cref="WebApplicationFactory{TEntryPoint}"/> only ever uses the type to find the assembly,
/// and any public type of that assembly answers the question.
/// </para>
/// <para>
/// Nothing is substituted. The composition root runs exactly as it does in production — the same
/// eleven module registrations for the control plane, the same five for the edge — and the only
/// thing the test controls is configuration. A test that replaced <c>ILinkStore</c> with a fake
/// would stop being an integration test somewhere around the first assertion.
/// </para>
/// </remarks>
public class DleTestHost<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <summary>Creates the host.</summary>
    /// <param name="settings">Configuration, which entirely replaces the shipped files.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public DleTestHost(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
    }

    /// <summary>
    /// A client that does not follow redirects, because the redirect is what most of these tests
    /// are asserting on.
    /// </summary>
    /// <returns>The client.</returns>
    public HttpClient CreateDirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Testing rather than Development: the shipped appsettings.Development.json turns on
        // developer-facing behaviour that no test should be asserting against.
        builder.UseEnvironment(Environments.Staging);

        // The test binary's own directory. Note that the shipped appsettings.json of the referenced
        // hosts IS copied beside the test binary by the build, so it is loaded as a base layer; every
        // key the tests care about is overridden below.
        builder.UseContentRoot(AppContext.BaseDirectory);

        // UseSetting, not ConfigureAppConfiguration + AddInMemoryCollection. For a host built with
        // WebApplication.CreateBuilder the factory applies ConfigureAppConfiguration only when the
        // host's Program calls Build(), which is after Program has already read the configuration
        // it uses eagerly at composition time - the NpgsqlDataSource singleton takes its connection
        // string then. Values bound lazily through IOptions saw the override; the connection string
        // did not, and the host under test connected to localhost:5432 from appsettings.json. Host
        // settings are applied before Program runs, so both paths see the same configuration.
        foreach ((string key, string? value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
