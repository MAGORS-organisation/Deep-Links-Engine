using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The domain routes of the control plane through HTTP against a real PostgreSQL (§B.4, FR-130 to
/// FR-136): registering a host with its normalisation, the consent-mode override that may only
/// tighten the tenant's mode, the one-host-one-tenant rule, the refusal to delete a host that still
/// serves links, and verification runs that record what DNS and the association files answered.
/// </summary>
/// <remarks>
/// Verification reaches out to the host it verifies. A host under the private test TLD does not
/// resolve, which is the failure every check has to report cleanly; <c>example.com</c> resolves
/// and answers 404 for the association files, which is the "reachable but not set up" shape an
/// operator sees before publishing the files.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class DomainsHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "FR-130")]
    public async Task Create_NormalisesTheHost_AndAnswers201WithAPendingDomain()
    {
        Fixture fixture = await SeedAsync("domains-create");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/domains",
            """{"host": "WWW.Links.Example.COM.", "is_default": true, "default_language": "sk"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement domain = document.RootElement;
        Guid id = domain.GetProperty("id").GetGuid();

        Assert.Equal("/api/v1/domains/" + id.ToString(), response.Headers.Location?.ToString());
        Assert.Equal("links.example.com", domain.GetProperty("host").GetString());
        Assert.True(domain.GetProperty("is_default").GetBoolean());
        Assert.True(domain.GetProperty("is_active").GetBoolean());
        Assert.Equal("pending", domain.GetProperty("aasa_status").GetString());
        Assert.Equal("pending", domain.GetProperty("assetlinks_status").GetString());
        Assert.Equal(fixture.TenantId, domain.GetProperty("tenant_id").GetGuid());
        Assert.False(
            domain.TryGetProperty("consent_mode_override", out JsonElement consentOverride)
            && consentOverride.ValueKind != JsonValueKind.Null,
            "a domain inherits the tenant consent mode unless one is set");

        Assert.Equal(
            "links.example.com",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT host FROM domains WHERE id = $1", [id], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-130")]
    public async Task Create_WithAnInternationalisedHost_StoresThePunycodeForm()
    {
        Fixture fixture = await SeedAsync("domains-idn");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/domains",
            """{"host": "odkazy.zákazník.example"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        string host = document.RootElement.GetProperty("host").GetString()!;
        Assert.StartsWith("odkazy.xn--", host, StringComparison.Ordinal);
        Assert.EndsWith(".example", host, StringComparison.Ordinal);
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-130")]
    [InlineData("""{"host": "not a host"}""", "host")]
    [InlineData("""{"host": ""}""", "host")]
    [InlineData("""{"host": "links.example.com", "consent_mode_override": "loose"}""", "consent_mode_override")]
    [InlineData("""{"host": "links.example.com", "consent_mode_override": "full"}""", "consent_mode_override")]
    public async Task Create_WithAnUnusableDescription_IsValidationFailedOnTheField(string body, string field)
    {
        // The tenant runs in aggregate_only; "full" would widen it, which an override may never do.
        Fixture fixture = await SeedAsync("domains-invalid-" + field);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/domains", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-131")]
    public async Task Create_WithATighterConsentOverride_StoresIt()
    {
        Fixture fixture = await SeedAsync("domains-consent");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/domains",
            """{"host": "quiet.example.com", "consent_mode_override": "OFF"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("off", document.RootElement.GetProperty("consent_mode_override").GetString());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-132")]
    public async Task Create_AHostAnotherTenantAlreadyServes_Is409DomainTaken()
    {
        Fixture fixture = await SeedAsync("domains-taken");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "domains-taken-other", cancellationToken: Ct);
        _ = await TestSeed.DomainAsync(Database, otherTenant, "shared.example.com", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/domains",
            """{"host": "Shared.Example.com"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.DomainTaken, document.RootElement.GetProperty("type").GetString());
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Get_AndList_ShowOnlyTheTenantsOwnDomains()
    {
        Fixture fixture = await SeedAsync("domains-scope");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "domains-scope-other", cancellationToken: Ct);
        Guid theirs = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("domains-scope-other"), cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage own = await fixture.Key.GetAsync(client, "/api/v1/domains/" + fixture.DomainId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await own.Content.ReadAsStringAsync(Ct));
        Assert.Equal(fixture.DomainHost, document.RootElement.GetProperty("host").GetString());

        using HttpResponseMessage foreign = await fixture.Key.GetAsync(client, "/api/v1/domains/" + theirs.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using HttpResponseMessage list = await fixture.Key.GetAsync(client, "/api/v1/domains", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(fixture.DomainId, page.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-131")]
    public async Task Patch_ChangesTheFlagsAndTheOverride_AndRefusesToWidenTheConsentMode()
    {
        Fixture fixture = await SeedAsync("domains-patch");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string path = "/api/v1/domains/" + fixture.DomainId.ToString();

        using HttpResponseMessage patched = await fixture.Key.PatchRawAsync(
            client,
            path,
            """{"is_default": true, "is_active": false, "consent_mode_override": "off"}""",
            Ct);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await patched.Content.ReadAsStringAsync(Ct));
        Assert.True(document.RootElement.GetProperty("is_default").GetBoolean());
        Assert.False(document.RootElement.GetProperty("is_active").GetBoolean());
        Assert.Equal("off", document.RootElement.GetProperty("consent_mode_override").GetString());
        Assert.False(await Sql.ScalarAsync<bool>(Database.DataSource, "SELECT is_active FROM domains WHERE id = $1", [fixture.DomainId], Ct));

        using HttpResponseMessage widened = await fixture.Key.PatchRawAsync(client, path, """{"consent_mode_override": "full"}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, widened.StatusCode);

        using HttpResponseMessage unknown = await fixture.Key.PatchRawAsync(client, "/api/v1/domains/" + Guid.NewGuid().ToString(), """{"is_active": true}""", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-133")]
    public async Task Delete_RefusesAHostThatStillServesLinks_AndRemovesAnEmptyOne()
    {
        Fixture fixture = await SeedAsync("domains-delete");
        Guid empty = await TestSeed.DomainAsync(Database, fixture.TenantId, TestSeed.UniqueHost("domains-delete-empty"), cancellationToken: Ct);
        _ = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "in-use", "https://example.com/", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage refused = await fixture.Key.DeleteAsync(client, "/api/v1/domains/" + fixture.DomainId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.DomainInUse, problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(1L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM domains WHERE id = $1", [fixture.DomainId], Ct));

        using HttpResponseMessage removed = await fixture.Key.DeleteAsync(client, "/api/v1/domains/" + empty.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM domains WHERE id = $1", [empty], Ct));

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, "/api/v1/domains/" + empty.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-134")]
    public async Task Verify_AHostThatDoesNotResolve_ReportsEveryCheckAsFailed_AndKeepsTheHistory()
    {
        Fixture fixture = await SeedAsync("domains-verify-unresolved");
        using HttpClient client = fixture.Host.CreateDirectClient();
        string path = "/api/v1/domains/" + fixture.DomainId.ToString();

        using HttpResponseMessage response = await fixture.Key.SendRawAsync(client, HttpMethod.Post, path + "/verify", null, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement run = document.RootElement;
        Assert.Equal(fixture.DomainId, run.GetProperty("domain_id").GetGuid());
        Assert.Equal(fixture.DomainHost, run.GetProperty("host").GetString());
        Assert.False(run.GetProperty("ok").GetBoolean());

        JsonElement checks = run.GetProperty("checks");
        Assert.True(checks.GetArrayLength() >= 3, "a verification run reports dns, tls and the two association files");
        JsonElement dns = FindCheck(checks, "dns");
        Assert.Equal("failed", dns.GetProperty("status").GetString());
        Assert.Contains(dns.GetProperty("codes").EnumerateArray(), code => code.GetString() == "dns.unresolved");

        using HttpResponseMessage history = await fixture.Key.GetAsync(client, path + "/verifications", Ct);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using JsonDocument records = JsonDocument.Parse(await history.Content.ReadAsStringAsync(Ct));
        JsonElement items = records.RootElement.GetProperty("items");
        Assert.Equal(checks.GetArrayLength(), items.GetArrayLength());
        Assert.Equal("failed", FindCheck(checks, "tls").GetProperty("status").GetString());

        // No application is paired with the host, so no association file is expected and those
        // two checks pass on their own terms. The history records every check as the run
        // reported it.
        foreach (JsonElement check in checks.EnumerateArray())
        {
            string kind = check.GetProperty("kind").GetString()!;
            JsonElement recorded = Assert.Single(
                items.EnumerateArray(),
                item => string.Equals(item.GetProperty("kind").GetString(), kind, StringComparison.Ordinal));
            Assert.Equal(check.GetProperty("status").GetString(), recorded.GetProperty("status").GetString());
        }

        Assert.Equal(
            (long)checks.GetArrayLength(),
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM domain_verifications WHERE domain_id = $1",
                [fixture.DomainId],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-134")]
    public async Task Verify_AReachableHostWithoutAssociationFiles_PassesDnsAndFailsTheFiles()
    {
        // example.com resolves publicly and answers 404 for both association files. With an iOS
        // and an Android application paired, both files are expected: DNS passes, the transport
        // passes, and both file checks fail for the reason an operator has to fix.
        Fixture fixture = await SeedAsync("domains-verify-reachable");
        Guid id = await TestSeed.DomainAsync(Database, fixture.TenantId, "example.com", cancellationToken: Ct);
        _ = await TestSeed.AppAsync(Database, fixture.TenantId, id, "ios", "com.example.app", teamId: "ABCDE12345", cancellationToken: Ct);
        _ = await TestSeed.AppAsync(
            Database,
            fixture.TenantId,
            id,
            "android",
            "com.example.app",
            playSigningFingerprints: ["AB:5C:" + string.Join(":", Enumerable.Repeat("00", 30))],
            cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.SendRawAsync(
            client, HttpMethod.Post, "/api/v1/domains/" + id.ToString() + "/verify", null, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement run = document.RootElement;
        Assert.False(run.GetProperty("ok").GetBoolean());
        JsonElement checks = run.GetProperty("checks");
        Assert.Equal("ok", FindCheck(checks, "dns").GetProperty("status").GetString());
        Assert.Equal("failed", FindCheck(checks, "aasa").GetProperty("status").GetString());
        Assert.Equal("failed", FindCheck(checks, "assetlinks").GetProperty("status").GetString());
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Verify_AnotherTenantsDomain_Is404AndRecordsNothing()
    {
        Fixture fixture = await SeedAsync("domains-verify-foreign");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "domains-verify-foreign-other", cancellationToken: Ct);
        Guid theirs = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("domains-verify-foreign-other"), cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.SendRawAsync(
            client, HttpMethod.Post, "/api/v1/domains/" + theirs.ToString() + "/verify", null, null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM domain_verifications WHERE domain_id = $1", [theirs], Ct));
    }

    private static JsonElement FindCheck(JsonElement checks, string kind)
    {
        foreach (JsonElement check in checks.EnumerateArray())
        {
            if (string.Equals(check.GetProperty("kind").GetString(), kind, StringComparison.Ordinal))
            {
                return check;
            }
        }

        throw new Xunit.Sdk.XunitException("no verification check of kind " + kind);
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
        string DomainHost);
}
