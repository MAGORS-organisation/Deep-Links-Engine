using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Dle.Control.Configuration;
using Dle.Domain.Attribution;
using Dle.Domain.Crypto;

using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The deferred attribution path end to end: a click in the partitioned stream, an install referrer
/// carrying its identifier, and the attribution that follows (TC-141, TC-142, TC-143, TC-144,
/// TC-149).
/// </summary>
/// <remarks>
/// <para>
/// The click identifier is minted by the host's own <see cref="IClickIdCodec"/> and then decoded
/// again, so the click event is written at exactly the instant the identifier claims — which is what
/// makes the bounded lookup of §B.6.3 find it, and what makes the test independent of how much time
/// passes while it runs.
/// </para>
/// <para>
/// Everything else is the shipped composition: the SDK key authenticates through its own scheme, the
/// tenant scope middleware establishes the tenant, and the strategies run in the configured order.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class AttributionEndToEndTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("TestCase", "TC-141")]
    public async Task Resolve_WithAnInstallReferrerCarryingTheClickId_MatchesDeterministically()
    {
        Fixture fixture = await SeedAsync();

        (string clickId, DateTimeOffset occurredAt) = await MintClickAsync(fixture);

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using HttpResponseMessage response = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/resolve",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "install_id": "install-1",
                    "platform": "android",
                    "app_version": "1.0.0",
                    "referrer": "utm_source=google-play&utm_medium=organic&dl_cid={{clickId}}"
                  }
                  """),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.True(document.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(MatchTypeNames.InstallReferrer, document.RootElement.GetProperty("match_type").GetString());
        Assert.Equal(1.00m, document.RootElement.GetProperty("confidence").GetDecimal());
        Assert.Equal(clickId, document.RootElement.GetProperty("click_id").GetString());

        // The decision is stored with the evidence that produced it, because "why was this install
        // credited to this campaign" is a question that gets asked in a dispute (§B.5.3).
        IReadOnlyList<string> stored = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT a.match_type || '|' || a.confidence::text || '|' || a.click_id
            FROM attributions a
            JOIN installs i ON i.id = a.install_id
            WHERE i.install_id = 'install-1'
            """,
            cancellationToken: Ct);

        Assert.Single(stored);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{MatchTypeNames.InstallReferrer}|1.00|{clickId}"),
            stored[0]);

        Assert.True(occurredAt > DateTimeOffset.UnixEpoch);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-142")]
    public async Task Resolve_WithAReferrerThatCarriesNoClickId_ReportsNoMatchAndIsNotAnError()
    {
        Fixture fixture = await SeedAsync();

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using HttpResponseMessage response = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/resolve",
            """
            {
              "install_id": "organic-1",
              "platform": "android",
              "referrer": "utm_source=google-play&utm_medium=organic"
            }
            """,
            Ct);

        // An organic install is the common case, not a failure. The application has to be able to
        // carry on with its normal onboarding.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.False(document.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(MatchTypeNames.None, document.RootElement.GetProperty("match_type").GetString());
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-143")]
    public async Task Resolve_CalledTwiceForOneInstall_IsIdempotentAndCreditsTheClickOnce()
    {
        Fixture fixture = await SeedAsync();

        (string clickId, _) = await MintClickAsync(fixture);

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        string body = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"install_id":"install-1","platform":"android","referrer":"dl_cid={{clickId}}"}""");

        using (HttpResponseMessage first = await fixture.SdkKey.PostRawAsync(client, "/v1/resolve", body, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using HttpResponseMessage second = await fixture.SdkKey.PostRawAsync(client, "/v1/resolve", body, Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));

        Assert.True(document.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(MatchTypeNames.InstallReferrer, document.RootElement.GetProperty("match_type").GetString());

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM attributions", cancellationToken: Ct));

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM installs", cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-144")]
    public async Task Resolve_ForASecondInstallClaimingTheSameClick_ReportsNoMatch()
    {
        // One click is worth at most one install. The partial unique index on attributions(click_id)
        // is what makes this true under a race; this asserts the answer the SDK is given.
        Fixture fixture = await SeedAsync();

        (string clickId, _) = await MintClickAsync(fixture);

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using (HttpResponseMessage first = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/resolve",
            string.Create(CultureInfo.InvariantCulture, $$"""{"install_id":"install-1","platform":"android","referrer":"dl_cid={{clickId}}"}"""),
            Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using HttpResponseMessage second = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/resolve",
            string.Create(CultureInfo.InvariantCulture, $$"""{"install_id":"install-2","platform":"android","referrer":"dl_cid={{clickId}}"}"""),
            Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));

        Assert.False(document.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(MatchTypeNames.None, document.RootElement.GetProperty("match_type").GetString());

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM attributions WHERE click_id = $1",
                [clickId],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-167")]
    public async Task Resolve_WithATamperedClickIdInTheReferrer_IsNotMatchedAndIsRecordedAsTampered()
    {
        Fixture fixture = await SeedAsync();

        (string clickId, _) = await MintClickAsync(fixture);

        // One character changed. The identifier is authenticated, so a failed decode is a mutated
        // identifier rather than an unlucky one (T-05).
        string tampered = clickId[..^1] + (clickId[^1] == 'A' ? 'B' : 'A');

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using HttpResponseMessage response = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/resolve",
            string.Create(CultureInfo.InvariantCulture, $$"""{"install_id":"install-t","platform":"android","referrer":"dl_cid={{tampered}}"}"""),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.False(document.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(MatchTypeNames.None, document.RootElement.GetProperty("match_type").GetString());

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM attributions WHERE click_id IS NOT NULL",
                cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-149")]
    public async Task Events_WithALinkOpenForOneOfOurLinks_ProducesADirectOpenAttribution()
    {
        // TC-149. When the association files verify and the application is installed, the operating
        // system opens it and no HTTP request reaches the engine at all — so without this event the
        // click is never counted and re-engagement is understated exactly where it worked best
        // (§B.6.4). The application reports the URL it was handed, which is deterministic evidence,
        // hence confidence 1.00.
        Fixture fixture = await SeedAsync();

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using HttpResponseMessage response = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/events",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "install_id": "install-open",
                    "platform": "ios",
                    "events": [
                      { "type": "link_open", "url": "https://{{fixture.Host}}/{{fixture.Slug}}" }
                    ]
                  }
                  """),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(1, document.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("rejected").GetInt32());

        IReadOnlyList<string> stored = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT a.match_type || '|' || a.confidence::text || '|' || coalesce(a.link_id::text, '-')
            FROM attributions a
            JOIN installs i ON i.id = a.install_id
            WHERE i.install_id = 'install-open'
            """,
            cancellationToken: Ct);

        Assert.Single(stored);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{MatchTypeNames.DirectOpen}|1.00|{fixture.LinkId}"),
            stored[0]);

        // No click was read, so no click identifier is claimed.
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM attributions WHERE click_id IS NOT NULL",
                cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Events_WithALinkOpenForAnotherTenantsLink_IsTreatedAsAForeignUrl()
    {
        // A link of another tenant is treated exactly like a URL that is not ours at all. Anything
        // else would let one tenant confirm the existence of another's slug.
        Fixture fixture = await SeedAsync();

        Guid otherTenant = await TestSeed.TenantAsync(Database, "attr-other", cancellationToken: Ct);
        string otherHost = TestSeed.UniqueHost("attr-other");
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, otherHost, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", "https://other.example.test/", cancellationToken: Ct);

        using HttpClient client = fixture.TestHost.CreateDirectClient();

        using HttpResponseMessage response = await fixture.SdkKey.PostRawAsync(
            client,
            "/v1/events",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "install_id": "install-foreign",
                    "platform": "ios",
                    "events": [ { "type": "link_open", "url": "https://{{otherHost}}/theirs" } ]
                  }
                  """),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                """
                SELECT count(*)
                FROM attributions a
                JOIN installs i ON i.id = a.install_id
                WHERE i.install_id = 'install-foreign'
                  AND a.match_type = 'direct_open'
                """,
                cancellationToken: Ct));
    }

    /// <summary>
    /// Mints a click identifier with the host's own codec and writes the click it names.
    /// </summary>
    /// <remarks>
    /// The identifier is decoded straight back, so the row is written at exactly the instant the
    /// identifier claims. The attribution service bounds its lookup by that instant (§B.6.3), so a
    /// row written at "roughly now" instead would make the test depend on how long the seed took.
    /// </remarks>
    private async Task<(string ClickId, DateTimeOffset OccurredAt)> MintClickAsync(Fixture fixture)
    {
        IClickIdCodec codec = fixture.TestHost.Services.GetRequiredService<IClickIdCodec>();

        DateTime serverNow = await Sql.ScalarAsync<DateTime>(
            Database.DataSource,
            "SELECT (now() AT TIME ZONE 'UTC')",
            cancellationToken: Ct);

        string clickId = codec.New(new DateTimeOffset(serverNow, TimeSpan.Zero));

        Assert.True(codec.TryDecode(clickId, out DateTimeOffset occurredAt, out _));

        await TestSeed.ClickEventAsync(
            Database,
            fixture.TenantId,
            fixture.LinkId,
            clickId,
            occurredAt,
            ipPrefix: "203.0.113.0/24",
            osFamily: "Android",
            cancellationToken: Ct);

        return (clickId, occurredAt);
    }

    /// <summary>Seeds a tenant with a domain, a link, an Android application and an SDK key.</summary>
    private async Task<Fixture> SeedAsync()
    {
        string host = TestSeed.UniqueHost("attr");

        Guid tenantId = await TestSeed.TenantAsync(Database, "attr", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        Guid appId = await TestSeed.AppAsync(
            Database,
            tenantId,
            domainId,
            "android",
            "sk.example.app",
            storeUrl: "https://play.google.com/store/apps/details?id=sk.example.app",
            cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "campaign", "https://example.test/campaign", cancellationToken: Ct);

        DleTestHost<DleControlOptions> controlHost = StartControl();

        ControlCredentials sdkKey = await ControlCredentials.IssueSdkKeyAsync(controlHost, Database, tenantId, appId, Ct);

        return new Fixture(controlHost, sdkKey, tenantId, appId, linkId, host, "campaign");
    }

    /// <summary>What the seed produced.</summary>
    private sealed record Fixture(
        DleTestHost<DleControlOptions> TestHost,
        ControlCredentials SdkKey,
        Guid TenantId,
        Guid AppId,
        long LinkId,
        string Host,
        string Slug);
}
