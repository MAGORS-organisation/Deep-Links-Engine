using Dle.Control.Configuration;
using Dle.Control.Infrastructure;
using Dle.SecurityTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.SecurityTests.Hardening;

/// <summary>
/// Control-plane hardening regressions: OpenAPI/Scalar gating and baseline security headers in
/// Production (M-7), plus fallback console CSP hygiene (L-6).
/// </summary>
public sealed class ControlHardeningTests
{
    [Fact]
    public async Task Production_WithoutExposeApiDocs_DoesNotServeOpenApiOrScalar()
    {
        using var host = new ControlProdHost(exposeApiDocs: false);
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage openApi = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        using HttpResponseMessage scalar = await client.GetAsync("/scalar/v1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, openApi.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, scalar.StatusCode);
        Assert.Equal("nosniff", Assert.Single(openApi.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(openApi.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(openApi.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Production_WithExposeApiDocs_ServesOpenApiWithBaselineSecurityHeaders()
    {
        using var host = new ControlProdHost(exposeApiDocs: true);
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage openApi = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Equal("nosniff", Assert.Single(openApi.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(openApi.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(openApi.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Production_ByDefault_DoesNotServeMetrics()
    {
        using var host = new ControlProdHost(exposeApiDocs: false);
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/admin", true)]
    [InlineData("/admin/index.html", true)]
    [InlineData("/admin/links", true)]
    [InlineData("/index.html", false)]
    [InlineData("/console.css", false)]
    [InlineData("/console.js", false)]
    [InlineData("/api/v1/links", false)]
    [InlineData("/openapi/v1.json", false)]
    public void IsConsolePath_MatchesSpaAndRootOnly(string path, bool expected) =>
        Assert.Equal(expected, AdminConsoleSecurityHeaders.IsConsolePath(new PathString(path)));

    [Fact]
    public void AdminSpaHtml_UsesExternalAssetsOnlyWithoutInlineCodeOrStyles()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        string sourceHtml = File.ReadAllText(Path.Combine(repoRoot, "src", "Dle.Admin.Web", "index.html"));
        AssertExternalAssetsOnly(sourceHtml);

        string builtAdminIndex = Path.Combine(repoRoot, "src", "Dle.Control", "wwwroot", "admin", "index.html");
        if (File.Exists(builtAdminIndex))
        {
            AssertExternalAssetsOnly(File.ReadAllText(builtAdminIndex));
        }

        string legacyWwwroot = Path.Combine(repoRoot, "src", "Dle.Control", "wwwroot");
        Assert.False(File.Exists(Path.Combine(legacyWwwroot, "index.html")));
        Assert.False(File.Exists(Path.Combine(legacyWwwroot, "console.css")));
        Assert.False(File.Exists(Path.Combine(legacyWwwroot, "console.js")));
    }

    private static void AssertExternalAssetsOnly(string html)
    {
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<script type=\"module\" ", html, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ControlProdHost(bool exposeApiDocs) : WebApplicationFactory<DleControlOptions>
    {
        private IReadOnlyDictionary<string, string?> Settings =>
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Port=1;Database=dle_security_tests;Username=dle;Password=dle;Timeout=1;Command Timeout=1",
                ["ConnectionStrings:Valkey"] = string.Empty,
                ["Dle:Crypto:MasterSecret"] = "dle-security-tests-master-secret-000000000000000",
                ["Dle:Workers:Enabled"] = "false",
                ["Dle:Bootstrap:Enabled"] = "false",
                ["Dle:Control:ServeAdminSpa"] = "false",
                ["Dle:Control:ExposeApiDocs"] = exposeApiDocs ? "true" : "false",
                ["Dle:Telemetry:Otlp:Enabled"] = "false",
            };

        protected override IHost CreateHost(IHostBuilder builder) =>
            TestHostEnvironment.RunWithEnvironment(Settings, () => base.CreateHost(builder));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        }
    }
}
