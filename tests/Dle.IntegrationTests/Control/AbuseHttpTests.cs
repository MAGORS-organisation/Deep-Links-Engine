using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The notice-and-action path of §E.3 through HTTP against a real PostgreSQL (FR-245, DSA art. 16,
/// TC-103): anyone may report a short URL without a credential, the reporter's address is kept
/// only as a hash, a report for a URL that is not one of ours is accepted without becoming an
/// oracle, the tenant sees only its own reports, the instance operator triages every report with
/// a statement of reasons, and quarantine withdraws a link from service as a versioned change
/// with a webhook in the outbox.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class AbuseHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "FR-245")]
    public async Task Report_AShortUrl_IsAcceptedWithoutACredential_AndKeepsOnlyAHashOfTheReporter()
    {
        Fixture fixture = await SeedAsync("abuse-report");
        long linkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "reported", "https://example.com/", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await ReportAsync(
            client,
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "url": "https://{{fixture.DomainHost}}/reported",
                    "reason": "Phishing",
                    "details": "Imitates a bank login page.",
                    "reporter_email": "Someone@Example.com"
                  }
                  """));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Guid reportId = document.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("new", document.RootElement.GetProperty("status").GetString());

        Assert.Equal(
            "phishing",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT reason FROM abuse_reports WHERE id = $1", [reportId], Ct));
        Assert.Equal(
            linkId,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT link_id FROM abuse_reports WHERE id = $1", [reportId], Ct));
        Assert.Equal(
            fixture.TenantId,
            await Sql.ScalarAsync<Guid>(Database.DataSource, "SELECT tenant_id FROM abuse_reports WHERE id = $1", [reportId], Ct));

        // A hash, never the address: nothing in the row can be read back as an e-mail.
        Assert.True(
            await Sql.ScalarAsync<bool>(
                Database.DataSource,
                "SELECT reporter_email_hash IS NOT NULL AND octet_length(reporter_email_hash) >= 32 FROM abuse_reports WHERE id = $1",
                [reportId],
                Ct));
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM abuse_reports WHERE id = $1 AND position('example.com' in encode(reporter_email_hash, 'escape')) > 0",
                [reportId],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Threat", "T-07")]
    public async Task Report_AUrlThatIsNotOneOfOurLinks_IsAcceptedIdenticallyAndStoresNothing()
    {
        // The public form must not tell an attacker which slugs exist: an unknown URL gets the
        // same 202 and the same shape of body as a known one, and no row.
        Fixture fixture = await SeedAsync("abuse-unknown");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await ReportAsync(
            client,
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"url": "https://{{fixture.DomainHost}}/nothinghere", "reason": "spam"}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("new", document.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM abuse_reports WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-245")]
    [InlineData("""{"url": "", "reason": "spam"}""", "url")]
    [InlineData("""{"url": "https://example.com/x", "reason": "because"}""", "reason")]
    public async Task Report_WithAnUnusableDescription_IsValidationFailedOnTheField(string body, string field)
    {
        Fixture fixture = await SeedAsync("abuse-invalid-" + field);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await ReportAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task TenantList_ShowsOnlyReportsAgainstTheTenantsOwnLinks_AndFiltersByStatus()
    {
        Fixture fixture = await SeedAsync("abuse-tenant-list");
        long mine = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "mine", "https://example.com/", cancellationToken: Ct);
        Guid otherTenant = await TestSeed.TenantAsync(Database, "abuse-tenant-list-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("abuse-tenant-list-other"), cancellationToken: Ct);
        long theirs = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", "https://example.com/", cancellationToken: Ct);
        Guid myReport = await SeedReportAsync(mine, fixture.TenantId, "spam", "new");
        _ = await SeedReportAsync(mine, fixture.TenantId, "malware", "rejected");
        _ = await SeedReportAsync(theirs, otherTenant, "phishing", "new");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage all = await fixture.Key.GetAsync(client, "/api/v1/abuse-reports", Ct);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await all.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(page.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal(fixture.TenantId, item.GetProperty("tenant_id").GetGuid()));

        using HttpResponseMessage fresh = await fixture.Key.GetAsync(client, "/api/v1/abuse-reports?status=new", Ct);
        using JsonDocument filtered = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync(Ct));
        JsonElement only = Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(myReport, only.GetProperty("id").GetGuid());
        Assert.Equal(mine.ToString(CultureInfo.InvariantCulture), only.GetProperty("link_id").GetString());
        Assert.False(only.GetProperty("has_reporter_contact").GetBoolean());

        using HttpResponseMessage badStatus = await fixture.Key.GetAsync(client, "/api/v1/abuse-reports?status=whatever", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.3")]
    public async Task Triage_IsForTheInstanceOperator_SeesEveryTenant_AndDecidesWithReasons()
    {
        Fixture fixture = await SeedAsync("abuse-triage", asOperator: true);
        long mine = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "mine", "https://example.com/", cancellationToken: Ct);
        Guid otherTenant = await TestSeed.TenantAsync(Database, "abuse-triage-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("abuse-triage-other"), cancellationToken: Ct);
        long theirs = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", "https://example.com/", cancellationToken: Ct);
        _ = await SeedReportAsync(mine, fixture.TenantId, "spam", "new");
        Guid theirReport = await SeedReportAsync(theirs, otherTenant, "phishing", "new");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage queue = await fixture.Key.GetAsync(client, "/api/v1/admin/abuse-reports?status=new", Ct);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await queue.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, page.RootElement.GetProperty("items").GetArrayLength());

        string decisionPath = "/api/v1/admin/abuse-reports/" + theirReport.ToString() + "/decision";

        using HttpResponseMessage noReasons = await fixture.Key.PostRawAsync(client, decisionPath, """{"status": "confirmed"}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noReasons.StatusCode);

        using HttpResponseMessage badStatus = await fixture.Key.PostRawAsync(client, decisionPath, """{"status": "new", "resolution_note": "x"}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        using HttpResponseMessage decided = await fixture.Key.PostRawAsync(
            client, decisionPath, """{"status": "Confirmed", "resolution_note": "Verified against the bank's own notice."}""", Ct);
        Assert.Equal(HttpStatusCode.NoContent, decided.StatusCode);

        Assert.Equal(
            "confirmed",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT status FROM abuse_reports WHERE id = $1", [theirReport], Ct));
        Assert.NotNull(
            await Sql.ScalarAsync<DateTime?>(Database.DataSource, "SELECT resolved_at FROM abuse_reports WHERE id = $1", [theirReport], Ct));

        using HttpResponseMessage unknown = await fixture.Key.PostRawAsync(
            client, "/api/v1/admin/abuse-reports/" + Guid.NewGuid().ToString() + "/decision", """{"status": "rejected", "resolution_note": "x"}""", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.3")]
    public async Task Triage_IsRefusedToAnOrdinaryTenant()
    {
        Fixture fixture = await SeedAsync("abuse-triage-plain");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage queue = await fixture.Key.GetAsync(client, "/api/v1/admin/abuse-reports", Ct);
        using HttpResponseMessage quarantine = await fixture.Key.PostRawAsync(client, "/api/v1/admin/links/1/quarantine", """{"reason": "x"}""", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, quarantine.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-103")]
    public async Task Quarantine_WithdrawsTheLinkAsAVersionedChange_AndReleaseRestoresIt()
    {
        Fixture fixture = await SeedAsync("abuse-quarantine", asOperator: true);
        Guid customer = await TestSeed.TenantAsync(Database, "abuse-quarantine-customer", cancellationToken: Ct);
        Guid customerDomain = await TestSeed.DomainAsync(Database, customer, TestSeed.UniqueHost("abuse-quarantine-customer"), cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, customer, customerDomain, "bad-link", "https://example.com/", cancellationToken: Ct);
        ControlCredentials customerKey = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, customer, "owner", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        string path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/admin/links/{linkId}/quarantine");

        using HttpResponseMessage noReason = await fixture.Key.PostRawAsync(client, path, "{}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        using HttpResponseMessage quarantined = await fixture.Key.PostRawAsync(client, path, """{"reason": "Confirmed phishing (report 42)."}""", Ct);
        Assert.Equal(HttpStatusCode.NoContent, quarantined.StatusCode);

        Assert.NotNull(await Sql.ScalarAsync<DateTime?>(Database.DataSource, "SELECT quarantined_at FROM links WHERE id = $1", [linkId], Ct));
        Assert.Equal(2, await Sql.ScalarAsync<int>(Database.DataSource, "SELECT version FROM links WHERE id = $1", [linkId], Ct));
        Assert.Equal(
            "Quarantined by abuse enforcement.",
            await Sql.ScalarAsync<string>(
                Database.DataSource,
                "SELECT change_note FROM link_versions WHERE link_id = $1 ORDER BY version DESC LIMIT 1",
                [linkId],
                Ct));

        // The owning tenant no longer sees the link through the ordinary list, and the enforcement
        // audit trail names the reason.
        using HttpResponseMessage list = await customerKey.GetAsync(client, "/api/v1/links", Ct);
        using JsonDocument page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());

        using HttpResponseMessage again = await fixture.Key.PostRawAsync(client, path, """{"reason": "still bad"}""", Ct);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(2, await Sql.ScalarAsync<int>(Database.DataSource, "SELECT version FROM links WHERE id = $1", [linkId], Ct));

        using HttpResponseMessage released = await fixture.Key.PostRawAsync(client, path + "/release", """{"reason": "Appeal upheld."}""", Ct);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Null(await Sql.ScalarAsync<DateTime?>(Database.DataSource, "SELECT quarantined_at FROM links WHERE id = $1", [linkId], Ct));
        Assert.Equal(3, await Sql.ScalarAsync<int>(Database.DataSource, "SELECT version FROM links WHERE id = $1", [linkId], Ct));

        using HttpResponseMessage visibleAgain = await customerKey.GetAsync(client, "/api/v1/links", Ct);
        using JsonDocument restored = JsonDocument.Parse(await visibleAgain.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, restored.RootElement.GetProperty("items").GetArrayLength());

        using HttpResponseMessage unknown = await fixture.Key.PostRawAsync(client, "/api/v1/admin/links/999999999999/quarantine", """{"reason": "x"}""", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<HttpResponseMessage> ReportAsync(HttpClient client, string json)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/abuse-reports", UriKind.Relative));
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await client.SendAsync(request, Ct);
    }

    private async Task<Guid> SeedReportAsync(long linkId, Guid tenantId, string reason, string status)
    {
        return await Sql.ScalarAsync<Guid>(
            Database.DataSource,
            """
            INSERT INTO abuse_reports (link_id, tenant_id, reason, status)
            VALUES ($1, $2, $3, $4)
            RETURNING id
            """,
            [linkId, tenantId, reason, status],
            Ct);
    }

    private async Task<Fixture> SeedAsync(string name, bool asOperator = false)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl(settings =>
        {
            // The public form is limited per source address; a test that reports several times
            // from the same loopback address must not trip it.
            settings["Dle:Abuse:ReportsPerHourPerIp"] = "1000";

            if (asOperator)
            {
                settings["Dle:Control:InstanceTenantId"] = tenantId.ToString();
            }
        });
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId, domainId, host);
    }

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId,
        Guid DomainId,
        string DomainHost);
}
