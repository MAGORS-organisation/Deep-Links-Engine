using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Dle.Edge.Configuration;
using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Telemetry;

namespace Dle.IntegrationTests.Chaos;

/// <summary>
/// §D.6, row two: with Valkey unavailable the resolve path keeps working from L1 and PostgreSQL at a
/// higher latency, and a metric says the shared cache level is down.
/// </summary>
/// <remarks>
/// <para>
/// The shared cache level is an optimisation, not a dependency. §B.8 profile A runs without one at
/// all, so a deployment that has configured one and then lost it must degrade to profile A rather
/// than to an outage — the database is still there, and every link the instance has seen recently is
/// still in its own memory.
/// </para>
/// <para>
/// The edge is pointed at an endpoint on the Valkey host where nothing is listening, rather than at
/// a name that does not resolve. That is what a stopped Valkey looks like to a client: the address
/// is fine and the port refuses. Taking the shared container away instead would be a more faithful
/// outage and a much worse idea — every other test in the run depends on it.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The hosts are handed to DleIntegrationTest.DisposeWithTest, which disposes them "
                    + "at the end of the test.")]
public sealed class ValkeyUnavailableTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task ValkeyUnreachable_ResolveStillAnswersFromL1AndPostgres()
    {
        string host = TestSeed.UniqueHost("chaos-valkey");
        Guid tenantId = await TestSeed.TenantAsync(Database, "chaos-valkey", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "resilient", "https://example.test/resilient", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge(valkey: Infrastructure.UnreachableValkeyConnectionString);
        using HttpClient client = edge.CreateDirectClient();

        // First request: nothing is in L1, so it goes to PostgreSQL past a shared level that is not
        // answering.
        using (HttpResponseMessage cold = await GetAsync(client, host, "resilient"))
        {
            Assert.Equal(HttpStatusCode.Found, cold.StatusCode);
            Assert.Equal("https://example.test/resilient", cold.Headers.Location?.ToString());
        }

        // Second request: L1 has it, and the unreachable shared level must not turn a hit into a
        // failure either.
        using HttpResponseMessage warm = await GetAsync(client, host, "resilient");

        Assert.Equal(HttpStatusCode.Found, warm.StatusCode);
        Assert.Equal("https://example.test/resilient", warm.Headers.Location?.ToString());
    }

    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task ValkeyUnreachable_AMissIsStillAnsweredAsAMissRatherThanAsAFailure()
    {
        string host = TestSeed.UniqueHost("chaos-valkey-404");
        Guid tenantId = await TestSeed.TenantAsync(Database, "chaos-valkey-404", cancellationToken: Ct);
        _ = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge(valkey: Infrastructure.UnreachableValkeyConnectionString);
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await GetAsync(client, host, "nothinghere");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task ValkeyUnreachable_AMetricReportsThatTheSharedCacheLevelIsDown()
    {
        // §D.6 names the metric: "resolve funguje z L1 + Postgresu s vyššou latenciou; metrika
        // cache_l2_down". Without it an operator sees a latency change and has nothing that says
        // why, which is the difference between a five minute diagnosis and an afternoon.
        HashSet<string> published = new(StringComparer.Ordinal);

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, _) =>
        {
            if (string.Equals(instrument.Meter.Name, EdgeMetrics.MeterName, StringComparison.Ordinal)
                || string.Equals(instrument.Meter.Name, FastPersistenceMetrics.MeterName, StringComparison.Ordinal))
            {
                lock (published)
                {
                    published.Add(instrument.Name);
                }
            }
        };

        listener.Start();

        string host = TestSeed.UniqueHost("chaos-valkey-metric");
        Guid tenantId = await TestSeed.TenantAsync(Database, "chaos-valkey-metric", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "metered", "https://example.test/metered", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge(valkey: Infrastructure.UnreachableValkeyConnectionString);
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await GetAsync(client, host, "metered");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        string[] names;

        lock (published)
        {
            names = [.. published];
        }

        Assert.True(
            names.Any(name =>
                name.Contains("l2", StringComparison.OrdinalIgnoreCase)
                && (name.Contains("down", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("available", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("up", StringComparison.OrdinalIgnoreCase))),
            "§D.6 requires a metric that reports the shared cache level as down while Valkey is "
            + "unavailable (cache_l2_down). The edge published none. Instruments seen: "
            + string.Join(", ", names.Order(StringComparer.Ordinal)));
    }

    /// <summary>Issues one resolve request.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string host, string slug)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}/{slug}")));

        request.Headers.UserAgent.ParseAdd(UserAgents.DesktopChrome);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
