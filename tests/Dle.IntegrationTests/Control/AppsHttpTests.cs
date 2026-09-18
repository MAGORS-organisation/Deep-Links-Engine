using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The application routes of the control plane through HTTP against a real PostgreSQL (§B.4,
/// FR-140 to FR-146): registering an iOS or Android application with its identifiers and
/// certificate fingerprints, pairing it with the tenant's domains, the Play App Signing warning,
/// and the SDK keys an application authenticates with - issued once in the clear, stored as a
/// prefix and a hash, revocable.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class AppsHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private const string Sha256Lower = "ab5c2f9e7d1a4b3c8e6f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d";
    private const string Sha256Colons = "AB:5C:2F:9E:7D:1A:4B:3C:8E:6F:0A:1B:2C:3D:4E:5F:6A:7B:8C:9D:0E:1F:2A:3B:4C:5D:6E:7F:8A:9B:0C:1D";

    [RequiresDockerFact]
    [Trait("Spec", "FR-141")]
    public async Task Create_AnIosApplication_Is201WithTheLocationAndItsPairedDomain()
    {
        Fixture fixture = await SeedAsync("apps-ios");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/apps",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "platform": "iOS",
                    "bundle_id": "sk.example.app",
                    "team_id": "TEAM123456",
                    "store_id": "id123456789",
                    "store_url": "https://apps.apple.com/app/id123456789",
                    "custom_scheme": "ExampleApp",
                    "domain_ids": ["{{fixture.DomainId}}"]
                  }
                  """),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement app = document.RootElement;
        Guid id = app.GetProperty("id").GetGuid();

        Assert.Equal("/api/v1/apps/" + id.ToString(), response.Headers.Location?.ToString());
        Assert.Equal("ios", app.GetProperty("platform").GetString());
        Assert.Equal("sk.example.app", app.GetProperty("bundle_id").GetString());
        Assert.Equal("TEAM123456", app.GetProperty("team_id").GetString());
        Assert.Equal("exampleapp", app.GetProperty("custom_scheme").GetString());
        Assert.Equal(fixture.TenantId, app.GetProperty("tenant_id").GetGuid());
        Assert.Equal(1, app.GetProperty("domain_ids").GetArrayLength());
        Assert.Equal(fixture.DomainId, app.GetProperty("domain_ids")[0].GetGuid());
        Assert.Equal(0, app.GetProperty("warnings").GetArrayLength());

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM app_domains WHERE app_id = $1 AND domain_id = $2",
                [id, fixture.DomainId],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-142")]
    public async Task Create_AnAndroidApplicationWithoutAFingerprint_WarnsThatAppLinksCannotVerify()
    {
        Fixture fixture = await SeedAsync("apps-android-bare");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/apps",
            """{"platform": "android", "bundle_id": "sk.example.app", "store_id": "sk.example.app"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement warnings = document.RootElement.GetProperty("warnings");
        Assert.Equal(1, warnings.GetArrayLength());
        Assert.Contains("No signing certificate fingerprint", warnings[0].GetString(), StringComparison.Ordinal);
        Assert.False(
            document.RootElement.TryGetProperty("team_id", out JsonElement teamId) && teamId.ValueKind != JsonValueKind.Null,
            "an Android application has no team id");
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-142")]
    public async Task Create_AnAndroidApplicationWithAFingerprint_NormalisesItToColonSeparatedUppercase()
    {
        Fixture fixture = await SeedAsync("apps-android-fp");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/apps",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"platform": "android", "bundle_id": "sk.example.signed", "cert_fingerprints": ["{{Sha256Lower}}"]}"""),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement fingerprints = document.RootElement.GetProperty("cert_fingerprints");
        Assert.Equal(1, fingerprints.GetArrayLength());
        Assert.Equal(Sha256Colons, fingerprints[0].GetString());
        Assert.Equal(0, document.RootElement.GetProperty("warnings").GetArrayLength());
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-141")]
    [InlineData("""{"platform": "windows", "bundle_id": "sk.example.app"}""", "platform")]
    [InlineData("""{"platform": "ios", "bundle_id": "sk.example.app"}""", "team_id")]
    [InlineData("""{"platform": "android", "bundle_id": "   "}""", "bundle_id")]
    [InlineData("""{"platform": "android", "bundle_id": "sk.example.app", "cert_fingerprints": ["not-a-digest"]}""", "cert_fingerprints")]
    public async Task Create_WithAnInvalidDescription_IsValidationFailedOnTheField(string body, string field)
    {
        Fixture fixture = await SeedAsync("apps-invalid-" + field);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/apps", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM apps WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-141")]
    public async Task Create_TheSamePlatformAndBundleTwice_Is409AppTaken()
    {
        Fixture fixture = await SeedAsync("apps-duplicate");
        using HttpClient client = fixture.Host.CreateDirectClient();
        const string body = """{"platform": "android", "bundle_id": "sk.example.twice"}""";

        using HttpResponseMessage first = await fixture.Key.PostRawAsync(client, "/api/v1/apps", body, Ct);
        using HttpResponseMessage second = await fixture.Key.PostRawAsync(client, "/api/v1/apps", body, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.AppTaken, document.RootElement.GetProperty("type").GetString());
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Get_AndList_ShowOnlyTheTenantsOwnApplications()
    {
        Fixture fixture = await SeedAsync("apps-scope");
        Guid mine = await TestSeed.AppAsync(Database, fixture.TenantId, fixture.DomainId, "ios", "sk.example.mine", teamId: "TEAM000001", cancellationToken: Ct);
        Guid otherTenant = await TestSeed.TenantAsync(Database, "apps-scope-other", cancellationToken: Ct);
        Guid theirs = await TestSeed.AppAsync(Database, otherTenant, null, "ios", "sk.example.theirs", teamId: "TEAM000002", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage own = await fixture.Key.GetAsync(client, "/api/v1/apps/" + mine.ToString(), Ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await own.Content.ReadAsStringAsync(Ct));
        Assert.Equal("sk.example.mine", document.RootElement.GetProperty("bundle_id").GetString());
        Assert.Equal(fixture.DomainId, document.RootElement.GetProperty("domain_ids")[0].GetGuid());

        using HttpResponseMessage foreign = await fixture.Key.GetAsync(client, "/api/v1/apps/" + theirs.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using HttpResponseMessage list = await fixture.Key.GetAsync(client, "/api/v1/apps", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string body = await list.Content.ReadAsStringAsync(Ct);
        using JsonDocument page = JsonDocument.Parse(body);
        Assert.Equal(1, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("sk.example.theirs", body, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-145")]
    public async Task Patch_ReplacesThePairingsAndTheOptionalFields_IgnoringAnotherTenantsDomain()
    {
        Fixture fixture = await SeedAsync("apps-patch");
        Guid second = await TestSeed.DomainAsync(Database, fixture.TenantId, TestSeed.UniqueHost("apps-patch-second"), cancellationToken: Ct);
        Guid otherTenant = await TestSeed.TenantAsync(Database, "apps-patch-other", cancellationToken: Ct);
        Guid foreignDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("apps-patch-other"), cancellationToken: Ct);
        Guid id = await TestSeed.AppAsync(Database, fixture.TenantId, fixture.DomainId, "android", "sk.example.patch", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage patched = await fixture.Key.PatchRawAsync(
            client,
            "/api/v1/apps/" + id.ToString(),
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                  {
                    "custom_scheme": "PatchedApp",
                    "store_url": "https://play.google.com/store/apps/details?id=sk.example.patch",
                    "domain_ids": ["{{second}}", "{{foreignDomain}}"]
                  }
                  """),
            Ct);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await patched.Content.ReadAsStringAsync(Ct));
        Assert.Equal("patchedapp", document.RootElement.GetProperty("custom_scheme").GetString());
        JsonElement domains = document.RootElement.GetProperty("domain_ids");
        Assert.Equal(1, domains.GetArrayLength());
        Assert.Equal(second, domains[0].GetGuid());

        // The original pairing was replaced, the foreign domain never attached.
        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM app_domains WHERE app_id = $1", [id], Ct));
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM app_domains WHERE app_id = $1 AND domain_id = $2",
                [id, foreignDomain],
                Ct));

        using HttpResponseMessage unknown = await fixture.Key.PatchRawAsync(client, "/api/v1/apps/" + Guid.NewGuid().ToString(), """{"team_id": "X"}""", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-141")]
    public async Task Delete_Is204_AndThePairingsGoWithIt()
    {
        Fixture fixture = await SeedAsync("apps-delete");
        Guid id = await TestSeed.AppAsync(Database, fixture.TenantId, fixture.DomainId, "android", "sk.example.gone", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage deleted = await fixture.Key.DeleteAsync(client, "/api/v1/apps/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage gone = await fixture.Key.GetAsync(client, "/api/v1/apps/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, "/api/v1/apps/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM app_domains WHERE app_id = $1", [id], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-242")]
    public async Task SdkKeys_AreIssuedOnceInTheClear_ListedByPrefix_AndRevocable()
    {
        Fixture fixture = await SeedAsync("apps-sdk-keys");
        Guid id = await TestSeed.AppAsync(Database, fixture.TenantId, fixture.DomainId, "android", "sk.example.keys", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        string keysPath = "/api/v1/apps/" + id.ToString() + "/sdk-keys";

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(client, keysPath, "{}", Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid keyId = issued.RootElement.GetProperty("id").GetGuid();
        string secret = issued.RootElement.GetProperty("secret").GetString()!;
        string prefix = issued.RootElement.GetProperty("prefix").GetString()!;
        // dlk_<prefix>_<secret>: the prefix is the public part a list can show.
        Assert.Equal(prefix, secret.Split('_')[1]);
        Assert.Equal(id, issued.RootElement.GetProperty("app_id").GetGuid());

        // Only the prefix and a hash are stored; the secret itself is nowhere in the database.
        Assert.Equal(
            prefix,
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT key_prefix FROM sdk_keys WHERE id = $1", [keyId], Ct));
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM sdk_keys WHERE id = $1 AND encode(hash, 'hex') = encode(convert_to($2, 'UTF8'), 'hex')",
                [keyId, secret],
                Ct));

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, keysPath, Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        string listBody = await listed.Content.ReadAsStringAsync(Ct);
        using JsonDocument list = JsonDocument.Parse(listBody);
        JsonElement item = list.RootElement.GetProperty("items")[0];
        Assert.Equal(prefix, item.GetProperty("prefix").GetString());
        Assert.True(item.GetProperty("is_active").GetBoolean());
        Assert.DoesNotContain(secret, listBody, StringComparison.Ordinal);

        // The secret authenticates the SDK route until it is revoked.
        using (HttpResponseMessage resolved = await ResolveWithSdkKeyAsync(client, secret))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using HttpResponseMessage secondCreated = await fixture.Key.PostRawAsync(client, keysPath, "{}", Ct);
        using JsonDocument secondIssued = JsonDocument.Parse(await secondCreated.Content.ReadAsStringAsync(Ct));
        Guid secondId = secondIssued.RootElement.GetProperty("id").GetGuid();
        string secondSecret = secondIssued.RootElement.GetProperty("secret").GetString()!;

        using HttpResponseMessage revoked = await fixture.Key.DeleteAsync(client, keysPath + "/" + secondId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.False(await Sql.ScalarAsync<bool>(Database.DataSource, "SELECT is_active FROM sdk_keys WHERE id = $1", [secondId], Ct));

        using HttpResponseMessage refused = await ResolveWithSdkKeyAsync(client, secondSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using HttpResponseMessage unknownKey = await fixture.Key.DeleteAsync(client, keysPath + "/" + Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknownKey.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task SdkKeys_ForAnotherTenantsApplication_Are404()
    {
        Fixture fixture = await SeedAsync("apps-sdk-foreign");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "apps-sdk-foreign-other", cancellationToken: Ct);
        Guid theirs = await TestSeed.AppAsync(Database, otherTenant, null, "android", "sk.example.theirs", cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        string keysPath = "/api/v1/apps/" + theirs.ToString() + "/sdk-keys";

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(client, keysPath, "{}", Ct);
        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, keysPath, Ct);

        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, listed.StatusCode);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM sdk_keys WHERE app_id = $1", [theirs], Ct));
    }

    private static async Task<HttpResponseMessage> ResolveWithSdkKeyAsync(HttpClient client, string secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/v1/resolve", UriKind.Relative));
        request.Headers.Add(DleKeyAuthenticationOptions.SdkKeyHeader, secret);
        request.Content = new StringContent(
            """{"install_id": "install-keys", "platform": "android", "referrer": "utm_source=organic"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<Fixture> SeedAsync(string name)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl();
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId, domainId);
    }

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId,
        Guid DomainId);
}
