using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The bulk import, the simulator and the templates of the link API through HTTP against a real
/// PostgreSQL (§B.7.3, FR-104, FR-108, FR-109): a batch streamed in as NDJSON is answered row by
/// row and can be replayed by its idempotency key, a link's routing can be asked what it would do
/// for a given client without a click being recorded, and a template gives new links their target,
/// tags and UTM parameters.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class LinksAdvancedHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private const string SafeTarget = "https://example.com/landing";
    private const string BulkContentType = "application/x-ndjson";
    private const string IdempotencyHeader = "Idempotency-Key";

    [RequiresDockerFact]
    [Trait("Spec", "FR-103")]
    public async Task Bulk_CreatesEveryUsableRow_AndAnswersOneLinePerRowInOrder()
    {
        Fixture fixture = await SeedAsync("bulk-rows");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string batch = string.Join(
            "\n",
            Row("first", fixture.DomainId, "\"slug\": \"spring-a\""),
            string.Empty,
            Row("second", fixture.DomainId, "\"slug\": \"api\""),
            Row("third", fixture.DomainId, null));

        using HttpResponseMessage response = await PostBatchAsync(fixture.Key, client, batch, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BulkContentType, response.Content.Headers.ContentType?.MediaType);
        IReadOnlyList<JsonDocument> lines = await LinesOf(response);
        Assert.Equal(3, lines.Count);

        Assert.Equal("first", lines[0].RootElement.GetProperty("ref").GetString());
        Assert.True(lines[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "https://" + fixture.Host_ + "/spring-a",
            lines[0].RootElement.GetProperty("short_url").GetString());

        // A reserved word as a slug: the row fails on its own, the batch goes on.
        Assert.Equal("second", lines[1].RootElement.GetProperty("ref").GetString());
        Assert.False(lines[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ProblemCodes.SlugInvalid, lines[1].RootElement.GetProperty("error").GetString());

        Assert.Equal("third", lines[2].RootElement.GetProperty("ref").GetString());
        Assert.True(lines[2].RootElement.GetProperty("ok").GetBoolean());
        Assert.NotNull(lines[2].RootElement.GetProperty("id").GetString());

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE tenant_id = $1", [fixture.TenantId], Ct));

        foreach (JsonDocument line in lines)
        {
            line.Dispose();
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.7.3")]
    public async Task Bulk_WithAnIdempotencyKey_ReplaysTheSummaryInsteadOfImportingTwice()
    {
        Fixture fixture = await SeedAsync("bulk-idempotent");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string batch = string.Join(
            "\n",
            Row("a", fixture.DomainId, "\"slug\": \"replay-a\""),
            Row("b", fixture.DomainId, "\"slug\": \"replay-b\""));
        string key = Guid.NewGuid().ToString("N");

        using HttpResponseMessage first = await PostBatchAsync(fixture.Key, client, batch, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(BulkContentType, first.Content.Headers.ContentType?.MediaType);
        IReadOnlyList<JsonDocument> lines = await LinesOf(first);
        Assert.Equal(2, lines.Count);
        foreach (JsonDocument line in lines)
        {
            line.Dispose();
        }

        using HttpResponseMessage replay = await PostBatchAsync(fixture.Key, client, batch, key);

        // The second presentation of the key gets the batch's summary, not a second import: the
        // rows were streamed once and the outcome is what the caller who lost the first answer needs.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("application/json", replay.Content.Headers.ContentType?.MediaType);
        using JsonDocument summary = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, summary.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("failed").GetInt32());

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.9")]
    public async Task Bulk_BeyondTheRowLimit_ImportsUpToTheLimit_AndSaysSoOnTheNextRow()
    {
        Fixture fixture = await SeedAsync("bulk-limit", settings => settings["Dle:Control:BulkMaxRows"] = "2");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string batch = string.Join(
            "\n",
            Row("1", fixture.DomainId, null),
            Row("2", fixture.DomainId, null),
            Row("3", fixture.DomainId, null),
            Row("4", fixture.DomainId, null));

        using HttpResponseMessage response = await PostBatchAsync(fixture.Key, client, batch, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyList<JsonDocument> lines = await LinesOf(response);
        Assert.Equal(3, lines.Count);
        Assert.True(lines[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.True(lines[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.False(lines[2].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ProblemCodes.RateLimited, lines[2].RootElement.GetProperty("error").GetString());
        Assert.Contains("maximum of 2 rows", lines[2].RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE tenant_id = $1", [fixture.TenantId], Ct));

        foreach (JsonDocument line in lines)
        {
            line.Dispose();
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-103")]
    public async Task Bulk_WithARowThatIsNotJson_ReportsThatRow_AndImportsTheRest()
    {
        Fixture fixture = await SeedAsync("bulk-garbage");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string batch = string.Join(
            "\n",
            "this is not a link",
            """{"ref": "empty", "link": null}""",
            Row("good", fixture.DomainId, null));

        using HttpResponseMessage response = await PostBatchAsync(fixture.Key, client, batch, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyList<JsonDocument> lines = await LinesOf(response);
        Assert.Equal(3, lines.Count);
        Assert.False(lines[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ProblemCodes.ValidationFailed, lines[0].RootElement.GetProperty("error").GetString());
        Assert.False(lines[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("empty", lines[1].RootElement.GetProperty("ref").GetString());
        Assert.Contains("no link definition", lines[1].RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(lines[2].RootElement.GetProperty("ok").GetBoolean());

        foreach (JsonDocument line in lines)
        {
            line.Dispose();
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task Bulk_IsAWrite_SoAViewerIs403()
    {
        Fixture fixture = await SeedAsync("bulk-viewer");
        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await PostBatchAsync(viewer, client, Row("x", fixture.DomainId, null), idempotencyKey: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-129")]
    public async Task Simulate_ExplainsTheDecisionForAClient_WithoutRecordingAClick()
    {
        Fixture fixture = await SeedAsync("simulate");
        using HttpClient client = fixture.Host.CreateDirectClient();
        long id = await CreateRoutedLinkAsync(fixture, client, "routed");

        using HttpResponseMessage ios = await fixture.Key.GetAsync(client, LinkPath(id) + "/simulate?platform=ios&country=sk", Ct);
        Assert.Equal(HttpStatusCode.OK, ios.StatusCode);
        using JsonDocument iosAnswer = JsonDocument.Parse(await ios.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ios-store", iosAnswer.RootElement.GetProperty("matched_rule_id").GetString());
        Assert.Equal("store_ios", iosAnswer.RootElement.GetProperty("decision").GetString());
        Assert.StartsWith("https://example.com/store", iosAnswer.RootElement.GetProperty("store_url").GetString(), StringComparison.Ordinal);
        Assert.Equal("aggregate_only", iosAnswer.RootElement.GetProperty("consent_mode").GetString());
        Assert.True(iosAnswer.RootElement.GetProperty("trace").GetArrayLength() > 0, "the simulator explains its decision");

        using HttpResponseMessage android = await fixture.Key.GetAsync(client, LinkPath(id) + "/simulate?platform=android", Ct);
        Assert.Equal(HttpStatusCode.OK, android.StatusCode);
        using JsonDocument androidAnswer = JsonDocument.Parse(await android.Content.ReadAsStringAsync(Ct));
        Assert.Equal("default", androidAnswer.RootElement.GetProperty("matched_rule_id").GetString());
        Assert.Equal("web", androidAnswer.RootElement.GetProperty("decision").GetString());
        Assert.StartsWith(SafeTarget, androidAnswer.RootElement.GetProperty("url").GetString(), StringComparison.Ordinal);

        // The same question asked with a body instead of a query string gets the same answer.
        using HttpResponseMessage posted = await fixture.Key.PostRawAsync(
            client,
            LinkPath(id) + "/simulate",
            """{"platform": "ios", "language": "SK"}""",
            Ct);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        using JsonDocument postedAnswer = JsonDocument.Parse(await posted.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ios-store", postedAnswer.RootElement.GetProperty("matched_rule_id").GetString());

        // A simulation is a question, not a visit.
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM click_events WHERE link_id = $1", [id], Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Simulate_AnUnknownOrForeignLink_Is404_AndAViewerMaySimulate()
    {
        Fixture fixture = await SeedAsync("simulate-scope");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "simulate-scope-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("simulate-scope-other"), cancellationToken: Ct);
        long theirs = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", SafeTarget, cancellationToken: Ct);
        long mine = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "mine", SafeTarget, cancellationToken: Ct);
        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage foreign = await fixture.Key.GetAsync(client, LinkPath(theirs) + "/simulate?platform=ios", Ct);
        using HttpResponseMessage unknown = await fixture.Key.GetAsync(client, LinkPath(mine + 1_000_000) + "/simulate", Ct);
        using HttpResponseMessage nonsense = await fixture.Key.GetAsync(client, "/api/v1/links/not-a-number/simulate", Ct);
        using HttpResponseMessage asViewer = await viewer.GetAsync(client, LinkPath(mine) + "/simulate?platform=desktop", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonsense.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asViewer.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await asViewer.Content.ReadAsStringAsync(Ct));
        Assert.Equal("web", answer.RootElement.GetProperty("decision").GetString());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-108")]
    public async Task Templates_AreCreated_Read_Listed_Updated_AndDeleted()
    {
        Fixture fixture = await SeedAsync("templates-crud");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links/templates",
            """
            {
              "name": "  Spring campaign  ",
              "target_url": "https://example.com/spring",
              "tags": ["spring"],
              "utm": {"utm_source": "newsletter", "utm_campaign": "spring"}
            }
            """,
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid id = document.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("/api/v1/links/templates/" + id.ToString(), created.Headers.Location?.ToString());
        Assert.Equal("Spring campaign", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("https://example.com/spring", document.RootElement.GetProperty("target_url").GetString());
        Assert.Equal("newsletter", document.RootElement.GetProperty("utm").GetProperty("utm_source").GetString());

        using HttpResponseMessage found = await fixture.Key.GetAsync(client, "/api/v1/links/templates/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, "/api/v1/links/templates", Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using JsonDocument list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));
        JsonElement item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());

        using HttpResponseMessage updated = await fixture.Key.PatchRawAsync(
            client,
            "/api/v1/links/templates/" + id.ToString(),
            """{"name": "Spring campaign (2026)", "target_url": "https://example.com/spring-2026", "tags": ["spring", "2026"]}""",
            Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using JsonDocument edited = JsonDocument.Parse(await updated.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Spring campaign (2026)", edited.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, edited.RootElement.GetProperty("tags").GetArrayLength());

        using HttpResponseMessage deleted = await fixture.Key.DeleteAsync(client, "/api/v1/links/templates/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage gone = await fixture.Key.GetAsync(client, "/api/v1/links/templates/" + id.ToString(), Ct);
        using HttpResponseMessage deletedAgain = await fixture.Key.DeleteAsync(client, "/api/v1/links/templates/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-108")]
    public async Task Templates_WithoutAName_AreValidationFailedOnTheName_AndAViewerMayOnlyRead()
    {
        Fixture fixture = await SeedAsync("templates-validation");
        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage unnamed = await fixture.Key.PostRawAsync(client, "/api/v1/links/templates", """{"name": "   "}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await unnamed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, problem.RootElement.GetProperty("type").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("name", out _));

        using HttpResponseMessage tooLong = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links/templates",
            string.Create(CultureInfo.InvariantCulture, $$"""{"name": "{{new string('x', 201)}}"}"""),
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using HttpResponseMessage viewerCreate = await viewer.PostRawAsync(client, "/api/v1/links/templates", """{"name": "Nope"}""", Ct);
        using HttpResponseMessage viewerList = await viewer.GetAsync(client, "/api/v1/links/templates", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, viewerCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);

        using HttpResponseMessage unknown = await fixture.Key.GetAsync(client, "/api/v1/links/templates/" + Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-108")]
    public async Task ALinkCreatedFromATemplate_InheritsItsRules_DeeplinkPath_Tags_AndUtm()
    {
        Fixture fixture = await SeedAsync("templates-apply");
        using HttpClient client = fixture.Host.CreateDirectClient();

        // FR-108: a template is a campaign's prefilled UTM parameters and routing rules. The
        // target stays the caller's: every link still names where it goes.
        using HttpResponseMessage template = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links/templates",
            """
            {
              "name": "Autumn",
              "deeplink_path": "/autumn",
              "tags": ["autumn"],
              "utm": {"utm_source": "newsletter"},
              "routing_rules": [
                {"id": "ios-store", "when": {"platform": ["ios"]}, "then": {"action": "store_only", "store_url": "https://example.com/store"}},
                {"id": "default", "then": {"action": "web", "url": "https://example.com/autumn"}}
              ]
            }
            """,
            Ct);
        Assert.Equal(HttpStatusCode.Created, template.StatusCode);
        Guid templateId;
        using (JsonDocument document = JsonDocument.Parse(await template.Content.ReadAsStringAsync(Ct)))
        {
            templateId = document.RootElement.GetProperty("id").GetGuid();
        }

        // The domain, the target and the template: the rules, the deep link path, the tags and
        // the UTM parameters come from the template, and what the request does say is merged in.
        using HttpResponseMessage link = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            string.Create(
                CultureInfo.InvariantCulture,
                $$$"""{"domain_id": "{{{fixture.DomainId}}}", "target_url": "https://example.com/autumn-landing", "campaign_id": "{{{templateId}}}", "tags": ["october"], "utm": {"utm_medium": "email"}}"""),
            Ct);

        string body = await link.Content.ReadAsStringAsync(Ct);
        Assert.True(link.StatusCode == HttpStatusCode.Created, "expected 201, got " + ((int)link.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + body);
        using JsonDocument created = JsonDocument.Parse(body);
        JsonElement root = created.RootElement;
        Assert.Equal("https://example.com/autumn-landing", root.GetProperty("target_url").GetString());
        Assert.Equal("/autumn", root.GetProperty("deeplink_path").GetString());
        List<string> ruleIds = [.. root.GetProperty("routing_rules").EnumerateArray().Select(static rule => rule.GetProperty("id").GetString()!)];
        Assert.Equal(["ios-store", "default"], ruleIds);
        Assert.Equal(templateId, root.GetProperty("campaign_id").GetGuid());
        Assert.Equal("newsletter", root.GetProperty("utm").GetProperty("utm_source").GetString());
        Assert.Equal("email", root.GetProperty("utm").GetProperty("utm_medium").GetString());
        List<string> tags = [.. root.GetProperty("tags").EnumerateArray().Select(static tag => tag.GetString()!)];
        Assert.Contains("autumn", tags);
        Assert.Contains("october", tags);

        using HttpResponseMessage unknown = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"domain_id": "{{fixture.DomainId}}", "target_url": "{{SafeTarget}}", "campaign_id": "{{Guid.NewGuid()}}"}"""),
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("campaign_id", out _));
    }

    private static string LinkPath(long id) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/links/{id}");

    private static string Row(string reference, Guid domainId, string? extraMembers)
    {
        string extra = string.IsNullOrWhiteSpace(extraMembers) ? string.Empty : ", " + extraMembers;
        return string.Create(
            CultureInfo.InvariantCulture,
            $$$"""{"ref": "{{{reference}}}", "link": {"domain_id": "{{{domainId}}}", "target_url": "{{{SafeTarget}}}"{{{extra}}}}}""");
    }

    /// <summary>Streams a batch the way an importer does: NDJSON, one link per line.</summary>
    private static async Task<HttpResponseMessage> PostBatchAsync(
        ControlCredentials key,
        HttpClient client,
        string batch,
        string? idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/links/bulk", UriKind.Relative));
        request.Headers.Add(DleKeyAuthenticationOptions.ApiKeyHeader, key.Token);

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyHeader, idempotencyKey);
        }

        request.Content = new StringContent(batch, Encoding.UTF8, BulkContentType);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<IReadOnlyList<JsonDocument>> LinesOf(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        List<JsonDocument> lines = [];

        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add(JsonDocument.Parse(line));
        }

        return lines;
    }

    private static async Task<long> CreateRoutedLinkAsync(Fixture fixture, HttpClient client, string slug)
    {
        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/links",
            string.Create(
                CultureInfo.InvariantCulture,
                $$$"""
                {
                  "domain_id": "{{{fixture.DomainId}}}",
                  "slug": "{{{slug}}}",
                  "target_url": "{{{SafeTarget}}}",
                  "routing_rules": [
                    {"id": "ios-store", "when": {"platform": ["ios"]}, "then": {"action": "store_only", "store_url": "https://example.com/store"}},
                    {"id": "default", "then": {"action": "web", "url": "{{{SafeTarget}}}"}}
                  ]
                }
                """),
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        return long.Parse(document.RootElement.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
    }

    private async Task<Fixture> SeedAsync(string name, Action<Dictionary<string, string?>>? configure = null)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = configure is null ? StartControl() : StartControl(configure);
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
