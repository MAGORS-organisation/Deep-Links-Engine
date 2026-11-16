using System.Net;
using System.Net.Http.Headers;

using Dle.SecurityTests.Infrastructure;

using Xunit;

namespace Dle.SecurityTests.Hardening;

/// <summary>
/// <c>GET /metrics</c> is anonymous unless a scrape token is set, and an ingress that forwards every
/// path to the edge would publish it. It is therefore off by default, and a configured token is
/// enforced.
/// </summary>
public sealed class MetricsExposureTests
{
    private const string ClientAddress = "203.0.113.10";
    private const string ScrapeToken = "security-tests-scrape-token";

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/metrics/")]
    public async Task Edge_ByDefault_DoesNotServeMetrics(string path)
    {
        using EdgeApiFactory factory = new();
        using HttpClient client = factory.CreateClientFrom(ClientAddress);

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("# TYPE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edge_WithScrapeToken_RequiresTheBearerToken()
    {
        using EdgeApiFactory factory = new();
        factory.Overrides["Dle:Telemetry:ExposePrometheusMetrics"] = "true";
        factory.Overrides["Dle:Telemetry:PrometheusScrapeToken"] = ScrapeToken;
        using HttpClient client = factory.CreateClientFrom(ClientAddress);

        using HttpResponseMessage anonymous = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        using HttpResponseMessage authorized = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }
}
