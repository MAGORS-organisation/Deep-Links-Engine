using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dle.Control.Configuration;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The link routes of the control plane through HTTP against a real PostgreSQL (§B.7.1, FR-101 to
/// FR-108): create with a custom or generated slug, read, list with filters and keyset paging,
/// update with its revision history, archive, delete, and the idempotent write of §B.7.3.
/// </summary>
/// <remarks>
/// Every failure branch the write service can take is walked once, because each is a distinct
/// problem type a client branches on: <c>slug-invalid</c>, <c>slug-taken</c>,
/// <c>unsafe-target</c>, <c>missing-default-rule</c>, <c>invalid-routing-rules</c>,
/// <c>validation-failed</c> and the 404 that hides another tenant's domain. Targets that must pass
/// point at <c>example.com</c>, which resolves to public addresses; the safety checker refuses a
/// host that does not resolve, so a private test TLD is a failure case here, not a fixture.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed partial class LinksHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private const string SafeTarget = "https://example.com/landing";

    [RequiresDockerFact]
    [Trait("Spec", "FR-101")]
    public async Task Create_WithACustomSlug_Is201WithTheLocationAndTheLink()
    {
        Fixture fixture = await SeedAsync("links-create");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, """
                "slug": "Summer-Sale",
                "title": "Summer sale",
                "tags": ["summer", "print"],
                "utm": {"utm_source": "print"}
                """),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement link = document.RootElement;
        string id = link.GetProperty("id").GetString()!;

        Assert.Equal("/api/v1/links/" + id, response.Headers.Location?.ToString());
        Assert.Equal("summer-sale", link.GetProperty("slug").GetString());
        Assert.Equal(fixture.Host_, link.GetProperty("host").GetString());
        Assert.Equal("https://" + fixture.Host_ + "/summer-sale", link.GetProperty("short_url").GetString());
        Assert.Contains("summer-sale", link.GetProperty("qr_url").GetString(), StringComparison.Ordinal);
        Assert.Equal(SafeTarget, link.GetProperty("target_url").GetString());
        Assert.Equal("Summer sale", link.GetProperty("title").GetString());
        Assert.True(link.GetProperty("is_active").GetBoolean());
        Assert.Equal(1, link.GetProperty("version").GetInt32());
        Assert.Equal(1, link.GetProperty("routing_rules").GetArrayLength());
        Assert.Equal("print", link.GetProperty("utm").GetProperty("utm_source").GetString());
        Assert.Equal(2, link.GetProperty("tags").GetArrayLength());

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM link_versions WHERE link_id = $1",
                [long.Parse(id, CultureInfo.InvariantCulture)],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-102")]
    public async Task Create_WithoutASlug_GeneratesAnEightCharacterOne()
    {
        Fixture fixture = await SeedAsync("links-generated");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage first = await fixture.Key.PostRawAsync(client, "/api/v1/links", Body(fixture.DomainId), Ct);
        using HttpResponseMessage second = await fixture.Key.PostRawAsync(client, "/api/v1/links", Body(fixture.DomainId), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        string firstSlug = await SlugOf(first);
        string secondSlug = await SlugOf(second);

        Assert.Matches(GeneratedSlug(), firstSlug);
        Assert.Matches(GeneratedSlug(), secondSlug);
        Assert.NotEqual(firstSlug, secondSlug);
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-102")]
    [InlineData("api", "reserved by the engine")]
    [InlineData("abcdefgh", "confused with a generated one")]
    [InlineData("bad slug!", "characters that are not allowed")]
    [InlineData("ab", "confused with a generated one")]
    public async Task Create_WithAnUnusableSlug_Is400SlugInvalid(string slug, string reason)
    {
        Fixture fixture = await SeedAsync("links-slug-" + slug.Length.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, "\"slug\": " + JsonSerializer.Serialize(slug)),
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.SlugInvalid, reason);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-102")]
    public async Task Create_WithATakenSlug_Is409SlugTaken()
    {
        Fixture fixture = await SeedAsync("links-taken");
        _ = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "taken", SafeTarget, cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, "\"slug\": \"TAKEN\""),
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ProblemCodes.SlugTaken, "already in use");
    }

    [RequiresDockerTheory]
    [Trait("Threat", "T-01")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("https://localhost/")]
    [InlineData("https://does-not-resolve.dle.test/")]
    public async Task Create_WithAnUnsafeTarget_Is422UnsafeTarget(string target)
    {
        Fixture fixture = await SeedAsync("links-unsafe-" + target.Length.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"domain_id": "{{fixture.DomainId}}", "target_url": {{JsonSerializer.Serialize(target)}}}"""),
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ProblemCodes.UnsafeTarget, null);

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM links WHERE tenant_id = $1",
                [fixture.TenantId],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-105")]
    public async Task Create_WithRulesButNoDefaultRule_Is400MissingDefaultRule()
    {
        Fixture fixture = await SeedAsync("links-nodefault");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, """
                "routing_rules": [
                  {"id": "ios", "when": {"platform": ["ios"]}, "then": {"action": "web", "url": "https://example.com/ios"}}
                ]
                """),
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.MissingDefaultRule, "default rule");
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-127")]
    public async Task Create_WithAWebRuleThatHasNoUrl_Is400InvalidRoutingRules()
    {
        Fixture fixture = await SeedAsync("links-badrule");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, """
                "routing_rules": [
                  {"id": "default", "then": {"action": "web"}}
                ]
                """),
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.InvalidRoutingRules, null);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-101")]
    public async Task Create_WithAnAbsoluteDeeplinkPath_IsValidationFailedOnThatField()
    {
        Fixture fixture = await SeedAsync("links-deeplink");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, "\"deeplink_path\": \"https://evil.example.com/steal\""),
            Ct);

        JsonElement problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.ValidationFailed, null);
        Assert.True(problem.GetProperty("errors").TryGetProperty("deeplink_path", out JsonElement messages));
        Assert.Equal(1, messages.GetArrayLength());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-104")]
    public async Task Create_WithExpiryBeforeActivation_IsValidationFailedOnExpiresAt()
    {
        Fixture fixture = await SeedAsync("links-window");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, """
                "starts_at": "2026-10-01T00:00:00Z",
                "expires_at": "2026-09-01T00:00:00Z"
                """),
            Ct);

        JsonElement problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.ValidationFailed, null);
        Assert.True(problem.GetProperty("errors").TryGetProperty("expires_at", out _));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Create_OnAnotherTenantsDomain_Is404AndNever403()
    {
        Fixture fixture = await SeedAsync("links-foreign");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "links-foreign-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("links-foreign-other"), cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/links", Body(otherDomain), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE domain_id = $1", [otherDomain], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-242")]
    public async Task Create_WithAViewerKey_Is403AndWithoutAKey_Is401()
    {
        Fixture fixture = await SeedAsync("links-roles");
        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage forbidden = await viewer.PostRawAsync(client, "/api/v1/links", Body(fixture.DomainId), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using HttpResponseMessage allowedToRead = await viewer.GetAsync(client, "/api/v1/links", Ct);
        Assert.Equal(HttpStatusCode.OK, allowedToRead.StatusCode);

        // Editor is where the policy actually draws the line. Without this leg a policy raised to
        // admin or owner would pass the suite while locking out the role meant to do the writing.
        ControlCredentials editor = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "editor", Ct);
        using HttpResponseMessage written = await editor.PostRawAsync(client, "/api/v1/links", Body(fixture.DomainId), Ct);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        using HttpRequestMessage anonymous = new(HttpMethod.Post, new Uri("/api/v1/links", UriKind.Relative));
        anonymous.Content = new StringContent(Body(fixture.DomainId), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage unauthorized = await client.SendAsync(anonymous, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-101")]
    public async Task Get_ReturnsTheLinkWithItsHost_AndAnUnknownOrMalformedIdIs404()
    {
        Fixture fixture = await SeedAsync("links-get");
        long id = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "readme", SafeTarget, title: "Read me", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage found = await fixture.Key.GetAsync(client, LinkPath(id), Ct);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await found.Content.ReadAsStringAsync(Ct));
        Assert.Equal("readme", document.RootElement.GetProperty("slug").GetString());
        Assert.Equal("Read me", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(fixture.Host_, document.RootElement.GetProperty("host").GetString());

        using HttpResponseMessage unknown = await fixture.Key.GetAsync(client, LinkPath(id + 1_000_000), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using HttpResponseMessage malformed = await fixture.Key.GetAsync(client, "/api/v1/links/not-a-number", Ct);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-109")]
    public async Task List_FiltersBySearchTagsAndActivity_AndPagesWithACursor()
    {
        Fixture fixture = await SeedAsync("links-list");
        long alpha = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "alpha-one", SafeTarget, title: "Alpha launch", cancellationToken: Ct);
        long beta = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "beta-two", SafeTarget, title: "Beta launch", cancellationToken: Ct);
        long paused = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "gamma-off", SafeTarget, isActive: false, cancellationToken: Ct);
        _ = await Sql.ExecuteAsync(Database.DataSource, "UPDATE links SET tags = ARRAY['spring', 'print'] WHERE id = $1", [alpha], Ct);
        _ = await Sql.ExecuteAsync(Database.DataSource, "UPDATE links SET tags = ARRAY['spring'] WHERE id = $1", [beta], Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        Assert.Equal([alpha, beta, paused], await IdsOf(fixture.Key, client, "/api/v1/links"));
        Assert.Equal([alpha, beta], await IdsOf(fixture.Key, client, "/api/v1/links?isActive=true"));
        Assert.Equal([paused], await IdsOf(fixture.Key, client, "/api/v1/links?isActive=false"));
        Assert.Equal([beta], await IdsOf(fixture.Key, client, "/api/v1/links?search=beta"));
        Assert.Equal([alpha], await IdsOf(fixture.Key, client, "/api/v1/links?tags=spring,print"));
        Assert.Equal([alpha, beta], await IdsOf(fixture.Key, client, "/api/v1/links?tags=spring"));
        Assert.Empty(await IdsOf(fixture.Key, client, "/api/v1/links?domainId=" + Guid.NewGuid().ToString()));

        // Keyset paging: one item per page, the cursor carries the reader to the next.
        using HttpResponseMessage firstPage = await fixture.Key.GetAsync(client, "/api/v1/links?limit=1", Ct);
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using JsonDocument first = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, first.RootElement.GetProperty("items").GetArrayLength());
        string? cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        IReadOnlyList<long> secondPage = await IdsOf(fixture.Key, client, "/api/v1/links?limit=1&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Single(secondPage);
        Assert.NotEqual(IdOf(first), secondPage[0]);

        using HttpResponseMessage tooLarge = await fixture.Key.GetAsync(client, "/api/v1/links?limit=0", Ct);
        await AssertProblemAsync(tooLarge, HttpStatusCode.BadRequest, ProblemCodes.ValidationFailed, "page size");
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-107")]
    public async Task Patch_ChangesTheFields_BumpsTheVersion_AndTheHistoryRemembersWhy()
    {
        Fixture fixture = await SeedAsync("links-patch");
        using HttpClient client = fixture.Host.CreateDirectClient();

        // Created through the API, not seeded: version 1 of the history is written by the create,
        // and the point of this test is that the history runs from creation to the edit.
        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(fixture.DomainId, "\"slug\": \"editable-link\""),
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        long id;
        using (JsonDocument creation = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            id = long.Parse(creation.RootElement.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(1, creation.RootElement.GetProperty("version").GetInt32());
        }

        using HttpResponseMessage patched = await fixture.Key.PatchRawAsync(
            client,
            LinkPath(id),
            """
            {
              "title": "Edited",
              "tags": ["edited"],
              "expires_at": "2030-01-01T00:00:00Z",
              "expired_url": "https://example.com/expired",
              "change_note": "extended into 2030"
            }
            """,
            Ct);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await patched.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Edited", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("https://example.com/expired", document.RootElement.GetProperty("expired_url").GetString());
        Assert.Equal(SafeTarget, document.RootElement.GetProperty("target_url").GetString());

        using HttpResponseMessage history = await fixture.Key.GetAsync(client, LinkPath(id) + "/versions", Ct);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using JsonDocument versions = JsonDocument.Parse(await history.Content.ReadAsStringAsync(Ct));
        JsonElement items = versions.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("version").GetInt32());
        Assert.Equal("extended into 2030", items[0].GetProperty("change_note").GetString());
        Assert.Equal("Edited", items[0].GetProperty("snapshot").GetProperty("title").GetString());
        Assert.Equal(1, items[1].GetProperty("version").GetInt32());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-101")]
    public async Task Patch_OfTheTargetAlone_MovesTheRuleThatWasFollowingIt()
    {
        // The edge routes from the rule set alone; target_url is not read at resolve time. A link
        // created without rules is stored with a catch-all synthesised from its target, so a patch
        // of the target that left that rule behind would send every visitor to the old page while
        // the API, the history and the console all showed the new one.
        Fixture fixture = await SeedAsync("links-retarget");
        using HttpClient client = fixture.Host.CreateDirectClient();
        long id;

        using (HttpResponseMessage created = await fixture.Key.PostRawAsync(client, "/api/v1/links", Body(fixture.DomainId), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
            id = long.Parse(document.RootElement.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            JsonElement rule = Assert.Single(document.RootElement.GetProperty("routing_rules").EnumerateArray());
            Assert.Equal(SafeTarget, rule.GetProperty("then").GetProperty("url").GetString());
        }

        using HttpResponseMessage patched = await fixture.Key.PatchRawAsync(
            client,
            LinkPath(id),
            """{"target_url": "https://example.com/moved"}""",
            Ct);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await patched.Content.ReadAsStringAsync(Ct));
        Assert.Equal("https://example.com/moved", answer.RootElement.GetProperty("target_url").GetString());
        JsonElement moved = Assert.Single(answer.RootElement.GetProperty("routing_rules").EnumerateArray());
        Assert.Equal("https://example.com/moved", moved.GetProperty("then").GetProperty("url").GetString());

        // What the edge will read, not only what the API answered.
        Assert.Contains(
            "https://example.com/moved",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT routing_rules::text FROM links WHERE id = $1", [id], Ct),
            StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-101")]
    public async Task Patch_OfTheTargetAlone_LeavesAnAuthoredRuleSetAsWritten()
    {
        // The other half of the contract: where the caller wrote a rule set of their own, the two
        // fields are meant to be able to differ, and a target change must not rewrite their rules.
        Fixture fixture = await SeedAsync("links-authored-rules");
        using HttpClient client = fixture.Host.CreateDirectClient();
        long id;

        using (HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            Body(
                fixture.DomainId,
                """
                "routing_rules": [
                  {"id": "everyone", "then": {"action": "web", "url": "https://example.com/authored"}}
                ]
                """),
            Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
            id = long.Parse(document.RootElement.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        }

        using HttpResponseMessage patched = await fixture.Key.PatchRawAsync(
            client,
            LinkPath(id),
            """{"target_url": "https://example.com/elsewhere"}""",
            Ct);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await patched.Content.ReadAsStringAsync(Ct));
        Assert.Equal("https://example.com/elsewhere", answer.RootElement.GetProperty("target_url").GetString());
        JsonElement rule = Assert.Single(answer.RootElement.GetProperty("routing_rules").EnumerateArray());
        Assert.Equal("https://example.com/authored", rule.GetProperty("then").GetProperty("url").GetString());
    }

    [RequiresDockerFact]
    [Trait("Threat", "T-01")]
    public async Task Patch_WithAnUnsafeTarget_Is422AndLeavesTheLinkAlone()
    {
        Fixture fixture = await SeedAsync("links-patch-unsafe");
        long id = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "steady", SafeTarget, cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PatchRawAsync(
            client,
            LinkPath(id),
            """{"target_url": "http://169.254.169.254/latest/meta-data/"}""",
            Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ProblemCodes.UnsafeTarget, null);
        Assert.Equal(
            SafeTarget,
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT target_url FROM links WHERE id = $1", [id], Ct));
        Assert.Equal(
            1,
            await Sql.ScalarAsync<int>(Database.DataSource, "SELECT version FROM links WHERE id = $1", [id], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-107")]
    public async Task Patch_UnknownLink_Is404_AndAnotherTenantsLink_IsTheSame404()
    {
        Fixture fixture = await SeedAsync("links-patch-404");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "links-patch-404-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("links-patch-404-other"), cancellationToken: Ct);
        long theirs = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", SafeTarget, cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage unknown = await fixture.Key.PatchRawAsync(client, LinkPath(theirs + 1_000_000), """{"title": "x"}""", Ct);
        using HttpResponseMessage foreign = await fixture.Key.PatchRawAsync(client, LinkPath(theirs), """{"title": "x"}""", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Null(await Sql.ScalarAsync<string>(Database.DataSource, "SELECT title FROM links WHERE id = $1", [theirs], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-107")]
    public async Task Archive_DeactivatesTheLinkAndRecordsARevision()
    {
        Fixture fixture = await SeedAsync("links-archive");
        long id = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "retired", SafeTarget, cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage archived = await fixture.Key.SendRawAsync(client, HttpMethod.Post, LinkPath(id) + "/archive", null, null, Ct);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await archived.Content.ReadAsStringAsync(Ct));
        Assert.False(document.RootElement.GetProperty("is_active").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());

        Assert.False(await Sql.ScalarAsync<bool>(Database.DataSource, "SELECT is_active FROM links WHERE id = $1", [id], Ct));
        Assert.Equal(
            "Archived through the control plane.",
            await Sql.ScalarAsync<string>(
                Database.DataSource,
                "SELECT change_note FROM link_versions WHERE link_id = $1 ORDER BY version DESC LIMIT 1",
                [id],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-107")]
    public async Task Delete_Is204_AndTheLinkAndItsHistoryAreGone()
    {
        Fixture fixture = await SeedAsync("links-delete");
        using HttpClient client = fixture.Host.CreateDirectClient();

        // Created and edited through the API, so that there is a history to be gone: a seeded row
        // has no revisions, and asserting that none survive it would prove nothing.
        long id;

        using (HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client, "/api/v1/links", Body(fixture.DomainId, "\"slug\": \"doomed\""), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
            id = long.Parse(document.RootElement.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        }

        using (HttpResponseMessage edited = await fixture.Key.PatchRawAsync(
            client, LinkPath(id), """{"title": "Doomed"}""", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM link_versions WHERE link_id = $1", [id], Ct));

        using HttpResponseMessage deleted = await fixture.Key.DeleteAsync(client, LinkPath(id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage gone = await fixture.Key.GetAsync(client, LinkPath(id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, LinkPath(id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE id = $1", [id], Ct));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM link_versions WHERE link_id = $1", [id], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.7.3")]
    public async Task Create_WithTheSameIdempotencyKeyAndBody_ReplaysTheFirstAnswer()
    {
        Fixture fixture = await SeedAsync("links-idem");
        using HttpClient client = fixture.Host.CreateDirectClient();
        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["Idempotency-Key"] = "retry-" + Guid.NewGuid().ToString("N") };
        string body = Body(fixture.DomainId, "\"slug\": \"once-only\"");

        using HttpResponseMessage first = await fixture.Key.SendRawAsync(client, HttpMethod.Post, "/api/v1/links", body, headers, Ct);
        using HttpResponseMessage replay = await fixture.Key.SendRawAsync(client, HttpMethod.Post, "/api/v1/links", body, headers, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));

        // One link, not two: the second request never reached the handler.
        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.7.3")]
    public async Task Create_WithTheSameIdempotencyKeyAndADifferentBody_Is409IdempotencyConflict()
    {
        Fixture fixture = await SeedAsync("links-idem-conflict");
        using HttpClient client = fixture.Host.CreateDirectClient();
        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["Idempotency-Key"] = "reused-" + Guid.NewGuid().ToString("N") };

        using HttpResponseMessage first = await fixture.Key.SendRawAsync(
            client, HttpMethod.Post, "/api/v1/links", Body(fixture.DomainId, "\"slug\": \"first-body\""), headers, Ct);
        using HttpResponseMessage conflict = await fixture.Key.SendRawAsync(
            client, HttpMethod.Post, "/api/v1/links", Body(fixture.DomainId, "\"slug\": \"second-body\""), headers, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, ProblemCodes.IdempotencyConflict, null);

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    /// <summary>A generated slug: eight characters of the base62 alphabet, nothing else.</summary>
    [GeneratedRegex("^[0-9A-Za-z]{8}$")]
    private static partial Regex GeneratedSlug();

    private static string LinkPath(long id) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/links/{id}");

    /// <summary>A create body for the fixture's domain and the safe target, plus any extra members.</summary>
    private static string Body(Guid domainId, string? extraMembers = null)
    {
        string extra = string.IsNullOrWhiteSpace(extraMembers) ? string.Empty : ", " + extraMembers;
        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"domain_id": "{{domainId}}", "target_url": "{{SafeTarget}}"{{extra}}}""");
    }

    private static async Task<string> SlugOf(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("slug").GetString()!;
    }

    private static long IdOf(JsonDocument page) =>
        long.Parse(page.RootElement.GetProperty("items")[0].GetProperty("id").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<long>> IdsOf(ControlCredentials key, HttpClient client, string path)
    {
        using HttpResponseMessage response = await key.GetAsync(client, path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        List<long> ids = [];
        foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            ids.Add(long.Parse(item.GetProperty("id").GetString()!, CultureInfo.InvariantCulture));
        }

        // The list is newest first; the seed order is what the assertions are written in.
        ids.Sort();
        return ids;
    }

    /// <summary>Asserts a problem document and returns it for further inspection.</summary>
    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string? detailFragment)
    {
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == status,
            string.Create(CultureInfo.InvariantCulture, $"expected {(int)status}, got {(int)response.StatusCode}: {body}"));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement problem = document.RootElement.Clone();
        Assert.Equal(type, problem.GetProperty("type").GetString());

        if (detailFragment is not null)
        {
            Assert.Contains(detailFragment, problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        return problem;
    }

    private async Task<Fixture> SeedAsync(string name)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl();
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId, domainId, host);
    }

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId,
        Guid DomainId,
        string Host_);
}
