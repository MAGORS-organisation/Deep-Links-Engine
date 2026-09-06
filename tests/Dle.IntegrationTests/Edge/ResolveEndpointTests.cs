using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Dle.Domain.Routing;
using Dle.Edge.Configuration;

namespace Dle.IntegrationTests.Edge;

/// <summary>
/// The resolve path end to end: the real edge composition root, real PostgreSQL, real Valkey
/// (§B.6.1, ADR-009, §D.3).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is substituted. The requests go through the same middleware, the same classifier, the
/// same consent gate, the same routing engine and the same two-level cache a deployment runs, and
/// the assertions are on the HTTP response an operating system or a crawler would actually see.
/// </para>
/// <para>
/// Every test seeds its own host. The link cache is keyed by host and slug and lives as long as the
/// process, so a shared host would let one test's cached miss decide another test's outcome.
/// </para>
/// </remarks>
public sealed class ResolveEndpointTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("TestCase", "TC-101")]
    public async Task Resolve_ActiveLinkOnIosWithoutTheApp_ServesTheInterstitialThatOffersTheStore()
    {
        // TC-101: "interstitial alebo 302 na App Store s pt/ct". With the shipped Auto policy an
        // iOS browser gets the interstitial, because a bare 302 to the store would skip the one tap
        // that lets the operating system hand a Universal Link to an installed application (§A.2.6).
        const string StoreUrl = "https://apps.apple.com/app/id123456789?pt=1234&ct=autumn26&mt=8";

        string host = TestSeed.UniqueHost("tc101");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc101", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.AppAsync(Database, tenantId, domainId, "ios", "sk.example.app", "TEAM123456", StoreUrl, "example", cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "autumn",
            "https://example.test/autumn",
            TestRules.IosAppOrStore(StoreUrl, "/promo/autumn"),
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "autumn", UserAgents.IosSafari);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(Ct);

        // The store URL and its campaign parameters reach the page, which is what the user taps.
        Assert.Contains("apps.apple.com/app/id123456789", body, StringComparison.Ordinal);
        Assert.Contains("pt=1234", body, StringComparison.Ordinal);
        Assert.Contains("ct=autumn26", body, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-101")]
    public async Task Resolve_ActiveLinkOnIosWithInterstitialNever_Redirects302ToTheStoreWithPtAndCt()
    {
        // The other half of TC-101's "or": with the interstitial switched off for this rule the same
        // link answers 302 straight to the App Store, and the campaign parameters survive intact.
        const string StoreUrl = "https://apps.apple.com/app/id123456789?pt=1234&ct=autumn26&mt=8";

        string host = TestSeed.UniqueHost("tc101b");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc101b", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "autumn",
            "https://example.test/autumn",
            TestRules.IosAppOrStore(StoreUrl, "/promo/autumn", InterstitialMode.Never),
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "autumn", UserAgents.IosSafari);

        // 302 and never 301 (ADR-009, SHARED-KERNEL §17.1): a 301 is cached indefinitely and would
        // freeze an A/B split, outlive a campaign's expiry and survive a change of target.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        string location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.StartsWith("https://apps.apple.com/app/id123456789", location, StringComparison.Ordinal);
        Assert.Contains("pt=1234", location, StringComparison.Ordinal);
        Assert.Contains("ct=autumn26", location, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-102")]
    public async Task Resolve_UnknownSlug_AnswersExactlyAsItDoesForAnotherTenantsSlug()
    {
        // TC-102 and TC-166 in one: a slug that exists somewhere else must be indistinguishable from
        // a slug that exists nowhere. Anything that differs — a status, a byte of the body, a header
        // — is an oracle for enumerating other tenants' links.
        string hostA = TestSeed.UniqueHost("tc102-a");
        string hostB = TestSeed.UniqueHost("tc102-b");

        Guid tenantA = await TestSeed.TenantAsync(Database, "tc102-a", cancellationToken: Ct);
        Guid tenantB = await TestSeed.TenantAsync(Database, "tc102-b", cancellationToken: Ct);

        Guid domainA = await TestSeed.DomainAsync(Database, tenantA, hostA, cancellationToken: Ct);
        Guid domainB = await TestSeed.DomainAsync(Database, tenantB, hostB, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantA, domainA, "mine", "https://a.example.test/", cancellationToken: Ct);
        _ = await TestSeed.LinkAsync(Database, tenantB, domainB, "theirs", "https://b.example.test/", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage foreignSlug = await GetAsync(client, hostA, "theirs", UserAgents.DesktopChrome);
        using HttpResponseMessage unknownSlug = await GetAsync(client, hostA, "nothinghere", UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.NotFound, foreignSlug.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSlug.StatusCode);

        Assert.Equal(
            foreignSlug.Content.Headers.ContentType?.ToString(),
            unknownSlug.Content.Headers.ContentType?.ToString());

        Assert.Equal(
            await foreignSlug.Content.ReadAsStringAsync(Ct),
            await unknownSlug.Content.ReadAsStringAsync(Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-103")]
    public async Task Resolve_QuarantinedLink_Returns410WithAPageAndNoRedirect()
    {
        string host = TestSeed.UniqueHost("tc103");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc103", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "withdrawn",
            "https://phishing.example.test/",
            quarantinedAt: DateTimeOffset.UnixEpoch,
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "withdrawn", UserAgents.DesktopChrome);

        // 410 rather than 404: the link is withdrawn, not missing, and the difference is what lets
        // the page carry the route to contest it (§E.3).
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(Ct);

        // Whatever else the page says, it must not carry the target it was withdrawn for.
        Assert.DoesNotContain("phishing.example.test", body, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-104")]
    public async Task Resolve_ExpiredLinkWithAnExpiredUrl_Redirects302ToIt()
    {
        string host = TestSeed.UniqueHost("tc104");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc104", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "lastyear",
            "https://example.test/campaign",
            expiresAt: DateTimeOffset.UnixEpoch.AddYears(40),
            expiredUrl: "https://example.test/campaign-has-ended",
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "lastyear", UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.test/campaign-has-ended", response.Headers.Location?.ToString());
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-104")]
    public async Task Resolve_ExpiredLinkWithoutAnExpiredUrl_IsIndistinguishableFromOneThatNeverExisted()
    {
        string host = TestSeed.UniqueHost("tc104b");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc104b", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "lastyear",
            "https://example.test/campaign",
            expiresAt: DateTimeOffset.UnixEpoch.AddYears(40),
            cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage expired = await GetAsync(client, host, "lastyear", UserAgents.DesktopChrome);
        using HttpResponseMessage missing = await GetAsync(client, host, "neverexisted", UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(Ct),
            await expired.Content.ReadAsStringAsync(Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-106")]
    public async Task Resolve_FacebookExternalHit_Serves200HtmlWithOpenGraphTagsAndNoCampaignClick()
    {
        string host = TestSeed.UniqueHost("tc106");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc106", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "shared",
            "https://example.test/shared",
            ogJson: """{"title":"Autumn sale","description":"Everything, half price","image_url":"https://cdn.example.test/a.png"}""",
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "shared", UserAgents.FacebookExternalHit);

        // A crawler does not follow a 30x reliably and runs no script, so a redirected preview comes
        // out blank. It gets the document (ADR-009, FR-161).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("""property="og:title""", body, StringComparison.Ordinal);
        Assert.Contains("Autumn sale", body, StringComparison.Ordinal);
        Assert.Contains("""property="og:image""", body, StringComparison.Ordinal);
        Assert.Contains("""name="twitter:card""", body, StringComparison.Ordinal);

        // The click is recorded — the preview happened and the rules were evaluated — but it is
        // recorded as a bot, and every campaign report filters is_bot = false by default (FR-205).
        // That is what "no record in campaign statistics" means in a schema that keeps the row.
        await Eventually.TrueAsync(
            async () => await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE link_id = $1",
                [linkId],
                Ct) == 1L,
            "the crawler's click event to reach the click stream",
            Ct);

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE link_id = $1 AND is_bot = true AND decision = 'preview'",
                [linkId],
                Ct));

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE link_id = $1 AND is_bot = false",
                [linkId],
                Ct));
    }

    [RequiresDockerTheory]
    [Trait("TestCase", "TC-106")]
    [Trait("Spec", "D.2.1")]
    [InlineData(UserAgents.FacebookExternalHit)]
    [InlineData("Twitterbot/1.0")]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; Jakarta Commons-HttpClient/3.1)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("TelegramBot (like TwitterBot)")]
    public async Task Resolve_ForEveryLinkPreviewFetcher_Serves200HtmlWithOpenGraphTags(string userAgent)
    {
        // §D.2.1 lists the crawler check as the one automatable row of the device matrix. Each of
        // these fetchers renders a preview from the document and none of them follows a redirect
        // reliably, so a 30x here is a blank card in somebody's chat client.
        string host = TestSeed.UniqueHost("crawlers");
        Guid tenantId = await TestSeed.TenantAsync(Database, "crawlers", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "preview",
            "https://example.test/preview",
            ogJson: """{"title":"Shared link","description":"A description"}""",
            cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(host, "preview", userAgent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("""property="og:title""", body, StringComparison.Ordinal);
        Assert.Contains("Shared link", body, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-110")]
    public async Task Resolve_QueryParameters_AreForwardedByTheAllowlistWithTheClickIdAdded()
    {
        string host = TestSeed.UniqueHost("tc110");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc110", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "forwarded",
            "https://example.test/landing",
            utmJson: """{"utm_medium":"default-medium"}""",
            cancellationToken: Ct);

        // dl_consent=all is the consent management platform's signal. Without it the tenant's full
        // mode still yields no click-id linking, because ePrivacy art. 5(3) needs the visitor's own
        // signal and not the operator's configuration (SHARED-KERNEL §2).
        using HttpResponseMessage response = await GetAsync(
            host,
            "forwarded?utm_source=newsletter&fbclid=fb-123&not_on_the_list=evil&dl_consent=all",
            UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        string location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.StartsWith("https://example.test/landing", location, StringComparison.Ordinal);

        // On the allowlist (§E.6.3), so forwarded.
        Assert.Contains("utm_source=newsletter", location, StringComparison.Ordinal);
        Assert.Contains("fbclid=fb-123", location, StringComparison.Ordinal);

        // The link's own default, kept where the request said nothing.
        Assert.Contains("utm_medium=default-medium", location, StringComparison.Ordinal);

        // FR-128: our own click identifier, added last.
        Assert.Contains("dl_cid=", location, StringComparison.Ordinal);

        // Not on the allowlist, so it never reaches the target — and neither does the consent
        // parameter, which is ours and not the target's business.
        Assert.DoesNotContain("not_on_the_list", location, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", location, StringComparison.Ordinal);
        Assert.DoesNotContain("dl_consent", location, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-110")]
    public async Task Resolve_WithoutAConsentSignal_ForwardsUtmButNeverTheClickId()
    {
        // The same request without the consent signal. The tenant is on full, so the operator has
        // enabled attribution — and the visitor still has not consented to it, which is exactly the
        // row of the SHARED-KERNEL §2 matrix that says "Full and no signal behaves like
        // AggregateOnly".
        string host = TestSeed.UniqueHost("tc110b");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc110b", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "noconsent", "https://example.test/landing", cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(
            host,
            "noconsent?utm_source=newsletter",
            UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        string location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.Contains("utm_source=newsletter", location, StringComparison.Ordinal);
        Assert.DoesNotContain("dl_cid=", location, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "SHARED-KERNEL 17.4")]
    [Trait("TestCase", "TC-164")]
    public async Task Resolve_WithATargetInAQueryParameter_IgnoresItCompletely()
    {
        // TC-164: the open redirect. The target comes from the stored rule set and from nowhere
        // else, which is a structural property of RoutingUrlBuilder — it only ever edits the query
        // component of the stored URL — rather than a filter that could be got past.
        string host = TestSeed.UniqueHost("tc164");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc164", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "safe", "https://example.test/landing", cancellationToken: Ct);

        using HttpResponseMessage response = await GetAsync(
            host,
            "safe?to=https%3A%2F%2Fevil.example%2F&url=https%3A%2F%2Fevil.example%2F&redirect_uri=https%3A%2F%2Fevil.example%2F",
            UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        string location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.StartsWith("https://example.test/landing", location, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", location, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-166")]
    public async Task Resolve_WithThePreviewParameter_DecidesEverythingAndRecordsNoClick()
    {
        string host = TestSeed.UniqueHost("preview");
        Guid tenantId = await TestSeed.TenantAsync(Database, "preview", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "previewed", "https://example.test/landing", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage preview = await GetAsync(client, host, "previewed?_dl=preview", UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, preview.StatusCode);

        // A real request afterwards, so that the wait below has something to wait for and the
        // assertion is "one click, not two" rather than "no click yet".
        using HttpResponseMessage real = await GetAsync(client, host, "previewed", UserAgents.DesktopChrome);

        Assert.Equal(HttpStatusCode.Found, real.StatusCode);

        await Eventually.TrueAsync(
            async () => await ClickCountAsync(linkId) >= 1L,
            "the real request's click event to reach the click stream",
            Ct);

        Assert.Equal(1L, await ClickCountAsync(linkId));
    }

    /// <summary>Counts the click events recorded for a link.</summary>
    private async Task<long> ClickCountAsync(long linkId) =>
        await Sql.ScalarAsync<long>(
            Database.DataSource,
            "SELECT count(*) FROM click_events WHERE link_id = $1",
            [linkId],
            Ct);

    /// <summary>Issues one resolve request against a freshly started edge.</summary>
    private async Task<HttpResponseMessage> GetAsync(string host, string pathAndQuery, string userAgent)
    {
        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        return await GetAsync(client, host, pathAndQuery, userAgent);
    }

    /// <summary>Issues one resolve request through an existing client.</summary>
    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string host,
        string pathAndQuery,
        string userAgent)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}/{pathAndQuery}")));

        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        return await client.SendAsync(request, Ct);
    }
}
