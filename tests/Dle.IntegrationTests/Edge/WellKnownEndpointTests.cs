using System.Globalization;
using System.Net;
using System.Text.Json;

using Dle.Domain.WellKnown;
using Dle.Edge.Configuration;
using Dle.Edge.WellKnown;

namespace Dle.IntegrationTests.Edge;

/// <summary>
/// The two association files, served per host from the applications registered on the link domain
/// (FR-141, FR-142, §C.3.3, TC-121, TC-122).
/// </summary>
/// <remarks>
/// <para>
/// Both endpoints fail silently when they are wrong, which is what makes them worth an integration
/// test rather than a unit test of the builder. An empty document, a redirect on the way, or a
/// content type carrying a charset parameter all produce the same symptom — the application is
/// simply never opened — and none of them produce an error anywhere.
/// </para>
/// <para>
/// The documents are checked with the product's own <see cref="WellKnownValidator"/>, which is the
/// same code the nightly domain verification of FR-143 runs against a customer's live domain. If the
/// validator and the builder ever disagree, the customer finds out; here, the test does.
/// </para>
/// </remarks>
public sealed class WellKnownEndpointTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("TestCase", "TC-121")]
    public async Task Aasa_OnAVerifiedDomainWithAnIosApp_Is200ApplicationJsonWithNoRedirect()
    {
        string host = TestSeed.UniqueHost("tc121");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc121", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.AppAsync(
            Database,
            tenantId,
            domainId,
            "ios",
            "sk.example.app",
            "TEAM123456",
            "https://apps.apple.com/app/id1",
            "example",
            cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AppleAppSiteAssociation}")),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);

        // Apple is unforgiving about this one: "application/json; charset=utf-8" is not
        // "application/json", and the association silently never forms.
        Assert.Equal(WellKnownDocument.JsonContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentType?.CharSet);

        string body = await response.Content.ReadAsStringAsync(Ct);

        IReadOnlyList<WellKnownValidationIssue> issues = WellKnownValidator.ValidateAasa(
            body,
            response.Content.Headers.ContentType?.MediaType,
            (int)response.StatusCode,
            redirectCount: 0,
            ["TEAM123456.sk.example.app"]);

        Assert.DoesNotContain(issues, issue => issue.IsError);

        // The Apple shape, checked structurally as well as through the validator.
        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement details = document.RootElement
            .GetProperty("applinks")
            .GetProperty("details");

        Assert.Equal(JsonValueKind.Array, details.ValueKind);
        Assert.Equal("TEAM123456.sk.example.app", details[0].GetProperty("appID").GetString());
        Assert.Equal(JsonValueKind.Array, details[0].GetProperty("components").ValueKind);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-122")]
    public async Task Aasa_OnADomainWithNoIosApp_Is404AndNotAnEmptyDocument()
    {
        // TC-122 is the whole reason the handler answers 404: Apple accepts an empty but well formed
        // document as a valid negative answer and caches it for about a week, during which the
        // domain's universal links are simply dead. A 404 is retried.
        string host = TestSeed.UniqueHost("tc122");
        Guid tenantId = await TestSeed.TenantAsync(Database, "tc122", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        // An Android application on the same host, so the domain is real and only the iOS side is
        // missing — the case that would otherwise produce "applinks with an empty details array".
        _ = await TestSeed.AppAsync(
            Database,
            tenantId,
            domainId,
            "android",
            "sk.example.app",
            playSigningFingerprints: ["AA:BB:CC"],
            cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AppleAppSiteAssociation}")),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("applinks", body, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-142")]
    public async Task AssetLinks_OnADomainWithAnAndroidApp_Is200WithThePlaySigningFingerprintFirst()
    {
        // FR-144: with Play App Signing the certificate that matters on a device is the one Google
        // re-signs with. Publishing only the local upload certificate is the single most common
        // reason App Links work in a debug build and fail in production (TC-123).
        string host = TestSeed.UniqueHost("assetlinks");
        Guid tenantId = await TestSeed.TenantAsync(Database, "assetlinks", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        Guid appId = await TestSeed.AppAsync(
            Database,
            tenantId,
            domainId,
            "android",
            "sk.example.app",
            playSigningFingerprints: ["11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00"],
            cancellationToken: Ct);

        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            "UPDATE apps SET cert_fingerprints = $2 WHERE id = $1",
            [
                appId,
                new[] { "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89" },
            ],
            Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AssetLinks}")),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WellKnownDocument.JsonContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentType?.CharSet);

        string body = await response.Content.ReadAsStringAsync(Ct);

        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement statement = document.RootElement[0];

        Assert.Equal("sk.example.app", statement.GetProperty("target").GetProperty("package_name").GetString());

        JsonElement fingerprints = statement.GetProperty("target").GetProperty("sha256_cert_fingerprints");

        Assert.Equal(2, fingerprints.GetArrayLength());
        Assert.StartsWith("11:22:33", fingerprints[0].GetString(), StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-142")]
    public async Task AssetLinks_OnADomainWithNoAndroidApp_Is404()
    {
        string host = TestSeed.UniqueHost("assetlinks-404");
        Guid tenantId = await TestSeed.TenantAsync(Database, "assetlinks-404", cancellationToken: Ct);
        _ = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AssetLinks}")),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "C.3.3")]
    public async Task WellKnownPaths_AreNeverAnsweredByTheSlugResolver()
    {
        // The prefix is reserved. A slug route that swallowed it would answer Apple with an HTML
        // 404 page, and the association would fail with no error anywhere (TC-121, TC-124).
        string host = TestSeed.UniqueHost("reserved");
        Guid tenantId = await TestSeed.TenantAsync(Database, "reserved", cancellationToken: Ct);
        _ = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdge();
        using HttpClient client = edge.CreateDirectClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}{WellKnownPaths.AppleAppSiteAssociation}")),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
