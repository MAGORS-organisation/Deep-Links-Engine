using Dle.Control.Configuration;
using Dle.SecurityTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.SecurityTests.Hardening;

/// <summary>
/// T-11: the administration console is served under a Content-Security-Policy that keeps foreign
/// scripts off the origin that holds an owner-capable key.
/// </summary>
public sealed class AdminConsoleHeaderTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    public static TheoryData<string> ConsolePaths() =>
    [
        "/admin/",            // the bundled index, served as a static file
        "/admin/links/42",    // a client-side route, served by the SPA fallback
    ];

    [Theory]
    [MemberData(nameof(ConsolePaths))]
    public async Task ConsolePages_CarryAStrictContentSecurityPolicy(string path)
    {
        using HttpClient client = _host.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? values));

        string policy = Assert.Single(values);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' https://login.example.test", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Theory]
    [InlineData("/admin/assets/app.js")]
    [InlineData("/admin/assets/app.css")]
    public async Task ConsoleAssets_AreServedWithoutACredential(string path)
    {
        // The browser fetches the bundle before anybody has signed in. The deny-by-default policy
        // exists for API endpoints that forgot to name one; applied to these files it leaves the
        // console as an empty page.
        using HttpClient client = _host.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task AnApiPathThatMatchesNoRoute_IsStillRefusedWithoutACredential()
    {
        // Serving the console's files anonymously must not open anything else.
        using HttpClient client = _host.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/no-such-route.json",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();

    private sealed class ConsoleHost : WebApplicationFactory<DleControlOptions>
    {
        private static readonly IReadOnlyDictionary<string, string?> Settings =
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Port=1;Database=dle_security_tests;Username=dle;Password=dle;Timeout=1;Command Timeout=1",
                ["ConnectionStrings:Valkey"] = string.Empty,
                ["Dle:Crypto:MasterSecret"] = "dle-security-tests-master-secret-000000000000000",
                ["Dle:Workers:Enabled"] = "false",
                ["Dle:Bootstrap:Enabled"] = "false",
                ["Dle:Control:ServeAdminSpa"] = "true",
                ["Dle:Control:SpaIndexFile"] = "admin/index.html",
                ["Dle:Identity:Oidc:Authority"] = "https://login.example.test/realms/dle",
                ["Dle:Identity:Oidc:ClientId"] = "dle-console",
                ["Dle:Identity:Oidc:Audience"] = "dle-console",
                ["Dle:Telemetry:Otlp:Enabled"] = "false",
            };

        // The built console is not in the repository (wwwroot/admin is a build output), so the host
        // gets a web root of its own with a stand-in index page.
        private readonly string _webRoot = Path.Combine(
            Path.GetTempPath(),
            "dle-console-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && Directory.Exists(_webRoot))
            {
                Directory.Delete(_webRoot, recursive: true);
            }
        }

        protected override IHost CreateHost(IHostBuilder builder) =>
            TestHostEnvironment.RunWithEnvironment(Settings, () => base.CreateHost(builder));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Path.Combine(_webRoot, "admin"));
            File.WriteAllText(
                Path.Combine(_webRoot, "admin", "index.html"),
                "<!doctype html><html><head><title>console</title></head><body><div id=\"root\"></div></body></html>");

            Directory.CreateDirectory(Path.Combine(_webRoot, "admin", "assets"));
            File.WriteAllText(Path.Combine(_webRoot, "admin", "assets", "app.js"), "console.log('console');");
            File.WriteAllText(Path.Combine(_webRoot, "admin", "assets", "app.css"), "body{margin:0}");

            builder.UseWebRoot(_webRoot);
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        }
    }
}
