using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// Tenants, API keys and the JWKS document through HTTP against a real PostgreSQL (§B.7.4, §E.4,
/// FR-240 to FR-244): a tenant reads itself, only the instance operator provisions, lists,
/// suspends and deletes tenants, keys are issued once in the clear with a role no stronger than
/// the issuer's, listed by prefix, and stop authenticating once they are revoked or their tenant
/// is suspended.
/// </summary>
/// <remarks>
/// The instance operator is whichever tenant <c>Dle:Control:InstanceTenantId</c> names; the tests
/// that need one start the host with that setting pointed at their own tenant.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class TenantsAndApiKeysHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "FR-241")]
    public async Task Me_ReturnsTheCallersOwnTenant_EvenForAViewer()
    {
        Fixture fixture = await SeedAsync("tenants-me", asOperator: false);
        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await viewer.GetAsync(client, "/api/v1/tenants/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(fixture.TenantId, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("tenants-me", document.RootElement.GetProperty("slug").GetString());
        Assert.Equal("active", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("aggregate_only", document.RootElement.GetProperty("consent_mode").GetString());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-241")]
    public async Task Tenants_AreListedAndReadOnlyByTheInstanceOperator()
    {
        Fixture plain = await SeedAsync("tenants-plain", asOperator: false);
        using HttpClient plainClient = plain.Host.CreateDirectClient();

        using HttpResponseMessage refused = await plain.Key.GetAsync(plainClient, "/api/v1/tenants", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Fixture operatorFixture = await SeedAsync("tenants-operator", asOperator: true);
        using HttpClient client = operatorFixture.Host.CreateDirectClient();

        using HttpResponseMessage list = await operatorFixture.Key.GetAsync(client, "/api/v1/tenants", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        Assert.True(page.RootElement.GetProperty("items").GetArrayLength() >= 2, "both seeded tenants are listed");

        using HttpResponseMessage one = await operatorFixture.Key.GetAsync(client, "/api/v1/tenants/" + plain.TenantId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await one.Content.ReadAsStringAsync(Ct));
        Assert.Equal("tenants-plain", document.RootElement.GetProperty("slug").GetString());

        using HttpResponseMessage unknown = await operatorFixture.Key.GetAsync(client, "/api/v1/tenants/" + Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-241")]
    public async Task Create_ATenant_Is201_NormalisesTheSlug_AndRefusesADuplicate()
    {
        Fixture fixture = await SeedAsync("tenants-create", asOperator: true);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/tenants",
            """{"slug": "  Acme-Corp ", "name": "Acme Corporation", "consent_mode": "FULL"}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid id = document.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("/api/v1/tenants/" + id.ToString(), created.Headers.Location?.ToString());
        Assert.Equal("acme-corp", document.RootElement.GetProperty("slug").GetString());
        Assert.Equal("Acme Corporation", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("full", document.RootElement.GetProperty("consent_mode").GetString());
        Assert.Equal("active", document.RootElement.GetProperty("status").GetString());

        using HttpResponseMessage duplicate = await fixture.Key.PostRawAsync(
            client, "/api/v1/tenants", """{"slug": "ACME-CORP", "name": "Again"}""", Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.Base + "tenant-slug-taken", problem.RootElement.GetProperty("type").GetString());

        using HttpResponseMessage defaulted = await fixture.Key.PostRawAsync(
            client, "/api/v1/tenants", """{"slug": "plain-tenant", "name": "Plain"}""", Ct);
        Assert.Equal(HttpStatusCode.Created, defaulted.StatusCode);
        using JsonDocument plain = JsonDocument.Parse(await defaulted.Content.ReadAsStringAsync(Ct));
        Assert.Equal("aggregate_only", plain.RootElement.GetProperty("consent_mode").GetString());
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-241")]
    [InlineData("""{"slug": "ab", "name": "Too short"}""", "slug")]
    [InlineData("""{"slug": "no name", "name": "Spaces are not allowed"}""", "slug")]
    [InlineData("""{"slug": "no-name", "name": "   "}""", "name")]
    [InlineData("""{"slug": "bad-consent", "name": "Bad", "consent_mode": "loose"}""", "consent_mode")]
    public async Task Create_ATenant_WithAnUnusableDescription_IsValidationFailedOnTheField(string body, string field)
    {
        Fixture fixture = await SeedAsync("tenants-invalid-" + field, asOperator: true);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/tenants", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-248")]
    public async Task Patch_ATenant_ChangesNameAndConsent_AndSuspensionLocksItsKeysOut()
    {
        Fixture fixture = await SeedAsync("tenants-patch", asOperator: true);
        Guid customer = await TestSeed.TenantAsync(Database, "tenants-patch-customer", cancellationToken: Ct);
        ControlCredentials customerKey = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, customer, "owner", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        string path = "/api/v1/tenants/" + customer.ToString();

        using HttpResponseMessage renamed = await fixture.Key.PatchRawAsync(client, path, """{"name": "Renamed", "consent_mode": "off"}""", Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Renamed", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("off", document.RootElement.GetProperty("consent_mode").GetString());

        using HttpResponseMessage invalidStatus = await fixture.Key.PatchRawAsync(client, path, """{"status": "deleted"}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);

        using HttpResponseMessage suspended = await fixture.Key.PatchRawAsync(client, path, """{"status": "suspended"}""", Ct);
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal(
            "suspended",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT status FROM tenants WHERE id = $1", [customer], Ct));

        // A suspended tenant's credential no longer authenticates anything.
        using HttpResponseMessage lockedOut = await customerKey.GetAsync(client, "/api/v1/links", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);

        using HttpResponseMessage unknown = await fixture.Key.PatchRawAsync(client, "/api/v1/tenants/" + Guid.NewGuid().ToString(), """{"name": "x"}""", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-241")]
    public async Task Delete_ATenant_Is204_AndTheTenantAndItsKeysVanish()
    {
        Fixture fixture = await SeedAsync("tenants-delete", asOperator: true);
        Guid customer = await TestSeed.TenantAsync(Database, "tenants-delete-customer", cancellationToken: Ct);
        ControlCredentials customerKey = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, customer, "owner", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        string path = "/api/v1/tenants/" + customer.ToString();

        using HttpResponseMessage deleted = await fixture.Key.DeleteAsync(client, path, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The operator can still read the row, marked deleted: the audit trail needs the name
        // and the slug, and a deleted tenant is not an unknown one.
        using HttpResponseMessage gone = await fixture.Key.GetAsync(client, path, Ct);
        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        using JsonDocument marked = JsonDocument.Parse(await gone.Content.ReadAsStringAsync(Ct));
        Assert.Equal("deleted", marked.RootElement.GetProperty("status").GetString());

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using HttpResponseMessage lockedOut = await customerKey.GetAsync(client, "/api/v1/links", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);

        // Soft deleted: the row stays for the audit trail, marked as such.
        Assert.Equal(
            "deleted",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT status FROM tenants WHERE id = $1", [customer], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task ApiKeys_AreIssuedOnceInTheClear_ListedByPrefix_ScopedAsRequested_AndRevocable()
    {
        Fixture fixture = await SeedAsync("keys-lifecycle", asOperator: false);
        Guid domainId = await TestSeed.DomainAsync(Database, fixture.TenantId, TestSeed.UniqueHost("keys-lifecycle"), cancellationToken: Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/api-keys",
            """{"name": "Reporting", "role": "Editor", "scopes": ["links:read"]}""",
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid keyId = issued.RootElement.GetProperty("id").GetGuid();
        string secret = issued.RootElement.GetProperty("secret").GetString()!;
        string prefix = issued.RootElement.GetProperty("prefix").GetString()!;
        Assert.Equal("editor", issued.RootElement.GetProperty("role").GetString());
        // dle_<prefix>_<secret>: the prefix is the public part a list can show.
        Assert.Equal(prefix, secret.Split('_')[1]);

        // Stored as a prefix and a hash, never as the secret.
        Assert.Equal(prefix, await Sql.ScalarAsync<string>(Database.DataSource, "SELECT prefix FROM api_keys WHERE id = $1", [keyId], Ct));

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, "/api/v1/api-keys", Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        string listBody = await listed.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(secret, listBody, StringComparison.Ordinal);
        using JsonDocument page = JsonDocument.Parse(listBody);
        JsonElement mine = FindKey(page.RootElement.GetProperty("items"), keyId);
        Assert.Equal("Reporting", mine.GetProperty("name").GetString());
        Assert.Equal(prefix, mine.GetProperty("prefix").GetString());
        Assert.Equal("links:read", mine.GetProperty("scopes")[0].GetString());
        Assert.False(
            mine.TryGetProperty("revoked_at", out JsonElement revokedAt) && revokedAt.ValueKind != JsonValueKind.Null,
            "a live key carries no revocation time");

        // The scope holds: the key reads links and cannot write them, whatever its role says.
        ControlCredentials scoped = ControlCredentials.FromToken(secret, fixture.TenantId);
        using (HttpResponseMessage read = await scoped.GetAsync(client, "/api/v1/links", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (HttpResponseMessage write = await scoped.PostRawAsync(
            client,
            "/api/v1/links",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{"domain_id": "{{domainId}}", "target_url": "https://example.com/"}"""),
            Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }

        using HttpResponseMessage revoked = await fixture.Key.DeleteAsync(client, "/api/v1/api-keys/" + keyId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, "/api/v1/api-keys/" + keyId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using HttpResponseMessage hidden = await fixture.Key.GetAsync(client, "/api/v1/api-keys", Ct);
        using JsonDocument active = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(active.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == keyId);

        using HttpResponseMessage shown = await fixture.Key.GetAsync(client, "/api/v1/api-keys?includeRevoked=true", Ct);
        using JsonDocument all = JsonDocument.Parse(await shown.Content.ReadAsStringAsync(Ct));
        Assert.NotEqual(JsonValueKind.Null, FindKey(all.RootElement.GetProperty("items"), keyId).GetProperty("revoked_at").ValueKind);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task ApiKeys_ARevokedKeyStopsAuthenticating()
    {
        Fixture fixture = await SeedAsync("keys-revoked", asOperator: false);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client, "/api/v1/api-keys", """{"name": "Short lived", "role": "viewer"}""", Ct);
        using JsonDocument issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid keyId = issued.RootElement.GetProperty("id").GetGuid();
        ControlCredentials shortLived = ControlCredentials.FromToken(issued.RootElement.GetProperty("secret").GetString()!, fixture.TenantId);

        using HttpResponseMessage revoked = await fixture.Key.DeleteAsync(client, "/api/v1/api-keys/" + keyId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // Never used before revocation, so no cached credential can vouch for it.
        using HttpResponseMessage refused = await shortLived.GetAsync(client, "/api/v1/tenants/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task ApiKeys_AKeyThatWasInUse_StopsAuthenticatingWhenTheCacheIsNotInTheWay()
    {
        // The test above revokes a key that was never presented, so the credential cache was never
        // asked about it. This one takes the path a leaked key really takes — used, then revoked,
        // then used again — with Dle:Identity:CredentialCacheSeconds at 0, which is the setting an
        // operator who needs revocation to bite at once has to choose. Left at its default of 60,
        // the same sequence answers 200 until the entry expires; that window is the documented
        // cost of not verifying a credential on every request (docs/self-hosting/configuration.md).
        Fixture fixture = await SeedAsync(
            "keys-revoked-live",
            asOperator: false,
            configure: settings => settings["Dle:Identity:CredentialCacheSeconds"] = "0");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client, "/api/v1/api-keys", """{"name": "In use", "role": "viewer"}""", Ct);
        using JsonDocument issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Guid keyId = issued.RootElement.GetProperty("id").GetGuid();
        ControlCredentials live = ControlCredentials.FromToken(issued.RootElement.GetProperty("secret").GetString()!, fixture.TenantId);

        using HttpResponseMessage before = await live.GetAsync(client, "/api/v1/tenants/me", Ct);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using HttpResponseMessage revoked = await fixture.Key.DeleteAsync(client, "/api/v1/api-keys/" + keyId.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using HttpResponseMessage after = await live.GetAsync(client, "/api/v1/tenants/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [RequiresDockerTheory]
    [Trait("Spec", "E.4.1")]
    [InlineData("""{"name": "   ", "role": "viewer"}""", "name")]
    [InlineData("""{"name": "Bad role", "role": "god"}""", "role")]
    [InlineData("""{"name": "Bad scope", "role": "viewer", "scopes": ["sdk:ingest"]}""", "scopes")]
    [InlineData("""{"name": "Expired", "role": "viewer", "expires_at": "2000-01-01T00:00:00Z"}""", "expires_at")]
    public async Task ApiKeys_WithAnUnusableDescription_IsValidationFailedOnTheField(string body, string field)
    {
        Fixture fixture = await SeedAsync("keys-invalid-" + field, asOperator: false);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/api-keys", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task ApiKeys_AnAdminMayListButOnlyAnOwnerMayIssue()
    {
        Fixture fixture = await SeedAsync("keys-roles", asOperator: false);
        ControlCredentials admin = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "admin", Ct);
        ControlCredentials editor = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "editor", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage adminList = await admin.GetAsync(client, "/api/v1/api-keys", Ct);
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);

        using HttpResponseMessage adminCreate = await admin.PostRawAsync(client, "/api/v1/api-keys", """{"name": "x", "role": "viewer"}""", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, adminCreate.StatusCode);

        using HttpResponseMessage editorList = await editor.GetAsync(client, "/api/v1/api-keys", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, editorList.StatusCode);

        // An owner issuing an owner key is allowed; nothing can issue a key above its own rank, and
        // the owner rank is the top, so the refusal is exercised through the role catalogue instead.
        using HttpResponseMessage ownerCreate = await fixture.Key.PostRawAsync(client, "/api/v1/api-keys", """{"name": "peer", "role": "owner"}""", Ct);
        Assert.Equal(HttpStatusCode.Created, ownerCreate.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.2")]
    public async Task Jwks_IsPublishedWithoutACredential()
    {
        Fixture fixture = await SeedAsync("jwks", asOperator: false);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/jwk-set+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("keys").ValueKind);
    }

    private static JsonElement FindKey(JsonElement items, Guid id)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == id)
            {
                return item;
            }
        }

        throw new Xunit.Sdk.XunitException("key " + id.ToString() + " is not listed");
    }

    private async Task<Fixture> SeedAsync(
        string name,
        bool asOperator,
        Action<Dictionary<string, string?>>? configure = null)
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl(settings =>
        {
            if (asOperator)
            {
                settings["Dle:Control:InstanceTenantId"] = tenantId.ToString();
            }

            configure?.Invoke(settings);
        });
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId);
    }

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId);
}
