using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Dle.Control.Configuration;
using Dle.Domain.Contracts;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The webhook routes of the control plane through HTTP against a real PostgreSQL (§B.7.5,
/// FR-230 to FR-234): a subscription is registered with a public https destination and a set of
/// known event types, its signing secret is shown once, a test delivery is attempted synchronously
/// and reported honestly, an event a link produces lands in the outbox once per subscription that
/// asked for it, and the deliveries are listed with their attempt state.
/// </summary>
/// <remarks>
/// Destinations are checked the way targets are: https only, a public address, a name that
/// resolves. <c>example.com</c> qualifies and answers every POST with an error, which is exactly
/// what the test delivery has to report; loopback, link-local and unresolvable hosts are the
/// refusals of T-02.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class WebhooksHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private const string Destination = "https://example.com/hooks/dle";

    [RequiresDockerFact]
    [Trait("Spec", "FR-230")]
    public async Task Create_ASubscription_ShowsTheSecretOnce_AndListsWithoutIt()
    {
        Fixture fixture = await SeedAsync("webhooks-create");
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/webhooks",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"url": "{{Destination}}", "event_types": ["link.quarantined", " link.released "]}"""),
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        JsonElement subscription = document.RootElement;
        Guid id = subscription.GetProperty("id").GetGuid();
        string secret = subscription.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(secret));
        Assert.Equal(Destination, subscription.GetProperty("url").GetString());
        Assert.True(subscription.GetProperty("is_active").GetBoolean());
        Assert.Equal(2, subscription.GetProperty("event_types").GetArrayLength());
        Assert.Equal("link.released", subscription.GetProperty("event_types")[1].GetString());

        // The secret is stored encrypted, never as the value shown to the caller.
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM webhook_subscriptions WHERE id = $1 AND position(convert_to($2, 'UTF8') in secret_encrypted) > 0",
                [id, secret],
                Ct));

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, "/api/v1/webhooks", Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        string listBody = await listed.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(secret, listBody, StringComparison.Ordinal);
        using JsonDocument list = JsonDocument.Parse(listBody);
        JsonElement item = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(Destination, item.GetProperty("url").GetString());
    }

    [RequiresDockerTheory]
    [Trait("Threat", "T-02")]
    [InlineData("http://example.com/hooks", "url")]
    [InlineData("https://localhost/hooks", "url")]
    [InlineData("https://127.0.0.1/hooks", "url")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "url")]
    [InlineData("https://hooks.does-not-resolve.dle.test/", "url")]
    [InlineData("not a url", "url")]
    public async Task Create_WithADestinationThatIsNotAPublicHttpsTarget_IsValidationFailedOnTheUrl(string url, string field)
    {
        Fixture fixture = await SeedAsync("webhooks-destination-" + url.Length.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/webhooks",
            string.Create(CultureInfo.InvariantCulture, $$"""{"url": {{JsonSerializer.Serialize(url)}}, "event_types": ["link.quarantined"]}"""),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM webhook_subscriptions WHERE tenant_id = $1", [fixture.TenantId], Ct));
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-231")]
    [InlineData("""{"url": "https://example.com/hooks", "event_types": []}""")]
    [InlineData("""{"url": "https://example.com/hooks", "event_types": ["link.exploded"]}""")]
    public async Task Create_WithUnknownOrMissingEventTypes_IsValidationFailedOnEventTypes(string body)
    {
        Fixture fixture = await SeedAsync("webhooks-events-" + body.Length.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("event_types", out _));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-230")]
    public async Task Create_BeyondTheTenantsSubscriptionLimit_Is409()
    {
        Fixture fixture = await SeedAsync("webhooks-limit", settings => settings["Dle:Webhooks:MaxSubscriptionsPerTenant"] = "2");
        using HttpClient client = fixture.Host.CreateDirectClient();
        const string body = """{"url": "https://example.com/hooks", "event_types": ["link.quarantined"]}""";

        using HttpResponseMessage first = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks", body, Ct);
        using HttpResponseMessage second = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks", body, Ct);
        using HttpResponseMessage third = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks", body, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.4.1")]
    public async Task Create_RequiresAnOwner_AndListingAnAdmin()
    {
        Fixture fixture = await SeedAsync("webhooks-roles");
        ControlCredentials admin = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "admin", Ct);
        ControlCredentials editor = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "editor", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        const string body = """{"url": "https://example.com/hooks", "event_types": ["link.quarantined"]}""";

        using HttpResponseMessage adminCreate = await admin.PostRawAsync(client, "/api/v1/webhooks", body, Ct);
        using HttpResponseMessage adminList = await admin.GetAsync(client, "/api/v1/webhooks", Ct);
        using HttpResponseMessage editorList = await editor.GetAsync(client, "/api/v1/webhooks", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, adminCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, editorList.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-233")]
    public async Task Test_DeliversASignedTestEventSynchronously_AndReportsTheEndpointsAnswer()
    {
        Fixture fixture = await SeedAsync("webhooks-test");
        using HttpClient client = fixture.Host.CreateDirectClient();
        Guid id = await CreateSubscriptionAsync(fixture, client, Destination, "link.quarantined");

        using HttpResponseMessage response = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks/" + id.ToString() + "/test", "{}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement result = document.RootElement;

        // example.com answers a POST with an error status: reachable, not accepted. That is an
        // honest "failed" with the status the endpoint gave, not an exception and not a success.
        Assert.False(result.GetProperty("delivered").GetBoolean());
        Assert.Equal("failed", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("response_code").GetInt32() >= 400);
        Assert.True(result.GetProperty("elapsed_ms").GetInt32() >= 0);
        using JsonDocument payload = JsonDocument.Parse(result.GetProperty("payload").GetString()!);
        Assert.Equal("webhook.test", payload.RootElement.GetProperty("event").GetString());

        using HttpResponseMessage unknown = await fixture.Key.PostRawAsync(client, "/api/v1/webhooks/" + Guid.NewGuid().ToString() + "/test", "{}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-232")]
    public async Task AQuarantine_LandsInTheOutboxOncePerSubscriptionThatAskedForIt()
    {
        Fixture fixture = await SeedAsync("webhooks-outbox");
        using HttpClient client = fixture.Host.CreateDirectClient();
        long linkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "watched", "https://example.com/", cancellationToken: Ct);
        Guid interested = await CreateSubscriptionAsync(fixture, client, Destination, "link.quarantined", "link.released");
        Guid indifferent = await CreateSubscriptionAsync(fixture, client, "https://example.com/other", "domain.verification_failed");

        using HttpResponseMessage quarantined = await fixture.Key.PostRawAsync(
            client,
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/admin/links/{linkId}/quarantine"),
            """{"reason": "Confirmed phishing."}""",
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, quarantined.StatusCode);

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, "/api/v1/webhooks/deliveries?includePayload=true", Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));
        JsonElement delivery = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(interested, delivery.GetProperty("subscription_id").GetGuid());
        Assert.Equal("link.quarantined", delivery.GetProperty("event_type").GetString());
        Assert.Equal("pending", delivery.GetProperty("status").GetString());
        Assert.Equal(0, delivery.GetProperty("attempt").GetInt32());
        using JsonDocument payload = JsonDocument.Parse(delivery.GetProperty("payload").GetString()!);
        Assert.Equal("link.quarantined", payload.RootElement.GetProperty("event").GetString());

        Assert.Equal(
            0L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM webhook_deliveries WHERE subscription_id = $1",
                [indifferent],
                Ct));

        using HttpResponseMessage filtered = await fixture.Key.GetAsync(client, "/api/v1/webhooks/deliveries?subscriptionId=" + indifferent.ToString(), Ct);
        using JsonDocument none = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, none.RootElement.GetArrayLength());
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-230")]
    public async Task Delete_RemovesTheSubscription_AndAnotherTenantsIs404()
    {
        Fixture fixture = await SeedAsync("webhooks-delete");
        Guid otherTenant = await TestSeed.TenantAsync(Database, "webhooks-delete-other", cancellationToken: Ct);
        ControlCredentials otherKey = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, otherTenant, "owner", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();
        Guid id = await CreateSubscriptionAsync(fixture, client, Destination, "link.quarantined");

        using HttpResponseMessage foreign = await otherKey.DeleteAsync(client, "/api/v1/webhooks/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using HttpResponseMessage deleted = await fixture.Key.DeleteAsync(client, "/api/v1/webhooks/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage again = await fixture.Key.DeleteAsync(client, "/api/v1/webhooks/" + id.ToString(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using HttpResponseMessage listed = await fixture.Key.GetAsync(client, "/api/v1/webhooks", Ct);
        using JsonDocument list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }

    private static async Task<Guid> CreateSubscriptionAsync(Fixture fixture, HttpClient client, string url, params string[] eventTypes)
    {
        using HttpResponseMessage created = await fixture.Key.PostRawAsync(
            client,
            "/api/v1/webhooks",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"url": {{JsonSerializer.Serialize(url)}}, "event_types": {{JsonSerializer.Serialize(eventTypes)}}}"""),
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Fixture> SeedAsync(string name, Action<Dictionary<string, string?>>? configure = null)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl(settings =>
        {
            // The outbox test quarantines through the operator route; every fixture may be the operator.
            settings["Dle:Control:InstanceTenantId"] = tenantId.ToString();
            configure?.Invoke(settings);
        });
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId, domainId);
    }

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId,
        Guid DomainId);
}
