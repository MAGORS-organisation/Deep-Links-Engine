using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Dle.Domain.Ports;
using Dle.Edge.Configuration;
using Dle.Edge.WellKnown;

using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Edge;

/// <summary>
/// The two-level link cache: what a control-plane change costs before the edge sees it, and how the
/// edge is told (ADR-005, §C.3.1, TC-125).
/// </summary>
/// <remarks>
/// <para>
/// There are two mechanisms and they answer different questions. Invalidation is the fast path: the
/// control plane drops the entry by key or by host tag and the next request reads through. Expiry is
/// the backstop for everything the control plane could not reach — another instance's L1, a message
/// that was lost — and it is what bounds the staleness a deployment tolerates.
/// </para>
/// <para>
/// Both are asserted, because a deployment that relied on invalidation alone would go stale forever
/// the first time a notification was dropped, and one that relied on expiry alone would take the
/// whole window to publish a correction to a phishing link.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The hosts are handed to DleIntegrationTest.DisposeWithTest, which disposes them "
                    + "at the end of the test.")]
public sealed class CacheBehaviourTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "ADR-005")]
    public async Task LinkChange_AfterTheControlPlaneInvalidatesIt_IsServedImmediately()
    {
        string host = TestSeed.UniqueHost("cache-invalidate");
        Guid tenantId = await TestSeed.TenantAsync(Database, "cache-invalidate", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "moving", "https://example.test/before", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using (HttpResponseMessage before = await GetAsync(client, host, "moving"))
        {
            Assert.Equal("https://example.test/before", before.Headers.Location?.ToString());
        }

        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            "UPDATE links SET target_url = 'https://example.test/after' WHERE id = $1",
            [linkId],
            Ct);

        // Still the old target: the snapshot is cached, which is the whole point of the cache.
        using (HttpResponseMessage stale = await GetAsync(client, host, "moving"))
        {
            Assert.Equal("https://example.test/before", stale.Headers.Location?.ToString());
        }

        await edge.Services
            .GetRequiredService<ILinkCacheInvalidator>()
            .InvalidateLinkAsync(host, "moving", Ct);

        using HttpResponseMessage after = await GetAsync(client, host, "moving");

        Assert.Equal("https://example.test/after", after.Headers.Location?.ToString());
    }

    [RequiresDockerFact]
    [Trait("Spec", "ADR-005")]
    public async Task LinkChange_WithNoInvalidationAtAll_IsServedOnceTheConfiguredWindowElapses()
    {
        // The backstop. Configured down to a second so the test can wait for it; in a deployment it
        // is Dle:Edge:Cache:L1Seconds and L2Minutes, and this asserts that the window is honoured
        // rather than that any particular number is right.
        string host = TestSeed.UniqueHost("cache-expiry");
        Guid tenantId = await TestSeed.TenantAsync(Database, "cache-expiry", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "expiring", "https://example.test/before", cancellationToken: Ct);

        // L1 only. With a shared level in play the window would be L2Minutes, whose smallest legal
        // value is a minute — too long to wait for and no more informative.
        DleTestHost<EdgeOptions> edge = StartEdge(
            settings => settings["Dle:Edge:Cache:L1Seconds"] = "1",
            valkey: string.Empty);

        using HttpClient client = edge.CreateDirectClient();

        using (HttpResponseMessage before = await GetAsync(client, host, "expiring"))
        {
            Assert.Equal("https://example.test/before", before.Headers.Location?.ToString());
        }

        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            "UPDATE links SET target_url = 'https://example.test/after' WHERE id = $1",
            [linkId],
            Ct);

        await Eventually.TrueAsync(
            async () =>
            {
                using HttpResponseMessage response = await GetAsync(client, host, "expiring");

                return string.Equals(
                    response.Headers.Location?.ToString(),
                    "https://example.test/after",
                    StringComparison.Ordinal);
            },
            "the one second L1 window to elapse and the new target to be served",
            Ct,
            TimeSpan.FromSeconds(15));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-125")]
    public async Task AssociationDocument_AfterTheHostIsInvalidated_IsRegenerated()
    {
        // TC-125: a rule change reaches the association file within fifteen minutes. The expiry is
        // the backstop; the host tag is the mechanism, and it is what makes the change immediate.
        string host = TestSeed.UniqueHost("cache-aasa");
        Guid tenantId = await TestSeed.TenantAsync(Database, "cache-aasa", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using (HttpResponseMessage before = await client.GetAsync(AasaUri(host), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        _ = await TestSeed.AppAsync(
            Database,
            tenantId,
            domainId,
            "ios",
            "sk.example.app",
            "TEAM123456",
            cancellationToken: Ct);

        await edge.Services
            .GetRequiredService<ILinkCacheInvalidator>()
            .InvalidateHostAsync(host, Ct);

        using HttpResponseMessage after = await client.GetAsync(AasaUri(host), Ct);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        string body = await after.Content.ReadAsStringAsync(Ct);

        Assert.Contains("TEAM123456.sk.example.app", body, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "ADR-005")]
    public async Task CachedSnapshot_SurvivesARestartOfTheEdgeBecauseTheSharedLevelHoldsIt()
    {
        // §B.8 profile B: L2 is what absorbs the cold start of a scaled-out replica. A second host
        // over the same Valkey has to find the snapshot without going back to PostgreSQL.
        string host = TestSeed.UniqueHost("cache-l2");
        Guid tenantId = await TestSeed.TenantAsync(Database, "cache-l2", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "shared", "https://example.test/shared", cancellationToken: Ct);

        DleTestHost<EdgeOptions> first = StartEdge();
        using (HttpClient firstClient = first.CreateDirectClient())
        using (HttpResponseMessage warm = await GetAsync(firstClient, host, "shared"))
        {
            Assert.Equal(HttpStatusCode.Found, warm.StatusCode);
        }

        // Change the row behind the cache. A second instance that reaches PostgreSQL would see the
        // new value; one that reads the shared cache sees the old one, which is what proves L2 was
        // consulted rather than the database.
        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            "UPDATE links SET target_url = 'https://example.test/changed' WHERE id = $1",
            [linkId],
            Ct);

        DleTestHost<EdgeOptions> second = StartEdge();
        using HttpClient secondClient = second.CreateDirectClient();

        using HttpResponseMessage fromL2 = await GetAsync(secondClient, host, "shared");

        Assert.Equal("https://example.test/shared", fromL2.Headers.Location?.ToString());
    }

    /// <summary>The association file's address on a host.</summary>
    private static Uri AasaUri(string host) =>
        new(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AppleAppSiteAssociation}"));

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
