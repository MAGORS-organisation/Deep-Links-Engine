using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Dle.Control.Configuration;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// TC-166 over HTTP: a key of one tenant asking about another tenant's link gets 404, never 403.
/// </summary>
/// <remarks>
/// <para>
/// SHARED-KERNEL §17.7 makes this a code review gate, and the reason is that 403 and 404 answer
/// different questions. A 403 says "this exists and you may not have it", which is an oracle: with
/// a key of my own I can walk the identifier space of every other customer and learn which links
/// exist, how many they have and, from a Snowflake identifier, roughly when they were created.
/// </para>
/// <para>
/// The mechanism is not a comparison in a handler. The caller's tenant is pushed into the ambient
/// tenant context by middleware and every query filter downstream reads it, so a foreign identifier
/// simply matches no row — which is why it is indistinguishable from one that never existed and why
/// the property holds for handlers nobody has written yet.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class TenantIsolationHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task GetLink_WithAnotherTenantsApiKey_Is404AndNever403()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        using HttpClient client = seeded.Host.CreateDirectClient();

        using HttpResponseMessage own = await seeded.KeyA.GetAsync(client, LinkPath(seeded.LinkA), Ct);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        using HttpResponseMessage foreign = await seeded.KeyA.GetAsync(client, LinkPath(seeded.LinkB), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, foreign.StatusCode);

        // And the same as a link that was never created at all, body included: anything that differs
        // is the oracle the 404 exists to close.
        using HttpResponseMessage absent = await seeded.KeyA.GetAsync(client, LinkPath(999_999_999_999L), Ct);

        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(
            await Sanitised(absent),
            await Sanitised(foreign));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task PatchLink_WithAnotherTenantsApiKey_Is404AndChangesNothing()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        using HttpClient client = seeded.Host.CreateDirectClient();

        using HttpRequestMessage request = new(
            HttpMethod.Patch,
            new Uri(LinkPath(seeded.LinkB), UriKind.Relative));

        request.Headers.Add("X-Api-Key", seeded.KeyA.Token);
        request.Content = new StringContent(
            """{"title":"taken over"}""",
            System.Text.Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Null(
            await Sql.ScalarAsync<string>(
                Database.DataSource,
                "SELECT title FROM links WHERE id = $1",
                [seeded.LinkB],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task ListLinks_WithATenantsApiKey_ReturnsOnlyThatTenantsLinks()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        using HttpClient client = seeded.Host.CreateDirectClient();

        using HttpResponseMessage response = await seeded.KeyA.GetAsync(client, "/api/v1/links", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement items = document.RootElement.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(
            seeded.LinkA.ToString(CultureInfo.InvariantCulture),
            items[0].GetProperty("id").GetString());

        Assert.DoesNotContain(
            seeded.LinkB.ToString(CultureInfo.InvariantCulture),
            body,
            StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-242")]
    public async Task AnyRequest_WithNoCredential_Is401AndNeverServesData()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        using HttpClient client = seeded.Host.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/links", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The management path of one link.</summary>
    private static string LinkPath(long id) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/links/{id}");

    /// <summary>
    /// The response body with the per-request correlation identifier removed.
    /// </summary>
    /// <remarks>
    /// An RFC 9457 document carries a trace identifier that is different on every request by design.
    /// Comparing two bodies verbatim would therefore always fail, and comparing nothing would miss
    /// the leak this test exists to catch.
    /// </remarks>
    private static async Task<string> Sanitised(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(body);

        Dictionary<string, JsonElement> members = new(StringComparer.Ordinal);

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("trace_id") || property.NameEquals("traceId") || property.NameEquals("instance"))
            {
                continue;
            }

            members[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(members, WireJson.Options);
    }

    /// <summary>Seeds two tenants with one link each, and an API key for the first.</summary>
    private async Task<Seeded> SeedTwoTenantsAsync()
    {
        Guid tenantA = await TestSeed.TenantAsync(Database, "iso-a", cancellationToken: Ct);
        Guid tenantB = await TestSeed.TenantAsync(Database, "iso-b", cancellationToken: Ct);

        Guid domainA = await TestSeed.DomainAsync(Database, tenantA, TestSeed.UniqueHost("iso-a"), cancellationToken: Ct);
        Guid domainB = await TestSeed.DomainAsync(Database, tenantB, TestSeed.UniqueHost("iso-b"), cancellationToken: Ct);

        long linkA = await TestSeed.LinkAsync(Database, tenantA, domainA, "mine", "https://a.example.test/", cancellationToken: Ct);
        long linkB = await TestSeed.LinkAsync(Database, tenantB, domainB, "theirs", "https://b.example.test/", cancellationToken: Ct);

        DleTestHost<DleControlOptions> host = StartControl();

        ControlCredentials keyA = await ControlCredentials.IssueApiKeyAsync(host, Database, tenantA, "owner", Ct);

        return new Seeded(host, keyA, linkA, linkB);
    }

    /// <summary>What the seed produced.</summary>
    private sealed record Seeded(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials KeyA,
        long LinkA,
        long LinkB);
}
