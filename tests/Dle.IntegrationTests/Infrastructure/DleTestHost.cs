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

        // The test binary's own directory. There is no wwwroot and no appsettings.json beside it,
        // which is the point: configuration comes from the dictionary below and from nowhere else.
        builder.UseContentRoot(AppContext.BaseDirectory);

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(_settings));
    }
}
