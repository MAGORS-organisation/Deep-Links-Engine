using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Dle.Control.Features.Shared;
using Dle.Domain.Contracts;
using Dle.Persistence.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.SecurityTests.Tenancy;

/// <summary>
/// S-02 / TC-166: a valid credential of one tenant, pointed at another tenant's resource, gets 404.
/// </summary>
/// <remarks>
/// <para>
/// Not 403. §E.2.2 T-09 calls cross-tenant leakage "vážny", and TC-166 spells out why the status code
/// is part of the mitigation: 403 confirms that the identifier names something. On a system whose
/// identifiers are Snowflakes and UUIDs that is a small leak, but it is a leak that compounds — a
/// competitor who can confirm which link identifiers exist can measure another tenant's campaign
/// volume without ever resolving one.
/// </para>
/// <para>
/// <b>What runs here and what does not.</b> The control-plane matrix — every tenant-scoped resource,
/// with tenant A's key against tenant B's identifier — is an HTTP test against a real deployment,
/// because the property is produced by PostgreSQL row level security, the EF Core query filters and
/// the tenant scope middleware acting together, and a substituted store would assert the substitute.
/// This machine has no PostgreSQL and no container runtime, so that test skips with a reason and says
/// how to run it. What does run is the part that is decidable without a database: the answer the
/// handlers give for "not yours", and the deny-by-default posture of the tenant context that makes a
/// query without a tenant impossible rather than global.
/// </para>
/// </remarks>
public sealed class CrossTenantAccessTests
{
    /// <summary>Base address of a running control plane, if one was supplied.</summary>
    public const string BaseUrlVariable = "DLE_TEST_CONTROL_URL";

    /// <summary>An API key belonging to tenant A.</summary>
    public const string TenantAKeyVariable = "DLE_TEST_TENANT_A_KEY";

    /// <summary>Resource identifiers belonging to tenant B, comma separated as <c>kind:id</c>.</summary>
    public const string TenantBResourcesVariable = "DLE_TEST_TENANT_B_RESOURCES";

    [Fact]
    [Trait("TestCase", "TC-166")]
    [Trait("Threat", "T-09")]
    public async Task NotFound_IsTheAnswerForAResourceThatIsNotYours()
    {
        // The shape of the answer, asserted where it is produced. Every handler that looks a resource
        // up answers with this one result for both "no such row" and "a row belonging to somebody
        // else", which is what makes the two indistinguishable — there is no second code path that
        // could drift into returning 403.
        (int status, JsonObject document) = await ExecuteAsync(DleProblemResults.NotFound("No such link."));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.NotEqual(StatusCodes.Status403Forbidden, status);
        Assert.Equal(ProblemCodes.Base + "not-found", document["type"]?.GetValue<string>());
        Assert.Equal("The resource does not exist.", document["title"]?.GetValue<string>());
    }

    [Theory]
    [Trait("TestCase", "TC-166")]
    [Trait("Threat", "T-09")]
    [InlineData("No such link.")]
    [InlineData("No such webhook subscription.")]
    public async Task NotFound_DoesNotEchoTheIdentifierItWasAskedAbout(string detail)
    {
        // A problem document is returned to a caller who may hold no credential for the resource at
        // all. Echoing the identifier back turns the document into a reflection point and, worse,
        // makes the two 404s distinguishable the moment somebody adds "…with id {id}" to one of them.
        (_, JsonObject document) = await ExecuteAsync(DleProblemResults.NotFound(detail));

        string written = document.ToJsonString();

        Assert.Equal(detail, document["detail"]?.GetValue<string>());
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", written, StringComparison.Ordinal);
        Assert.DoesNotContain("7286414500000000001", written, StringComparison.Ordinal);
    }

    /// <summary>Runs a minimal-API result and returns the status and body it wrote.</summary>
    /// <remarks>
    /// The result is executed rather than pattern matched, because what an integrator sees is the
    /// bytes on the wire and not the type that produced them — and because the type that produces them
    /// here is private, as it should be.
    /// </remarks>
    private static async Task<(int Status, JsonObject Document)> ExecuteAsync(IResult result)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddProblemDetails();

        await using ServiceProvider provider = services.BuildServiceProvider();

        DefaultHttpContext context = new()
        {
            RequestServices = provider,
        };

        using MemoryStream body = new();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;

        JsonObject document = JsonNode.Parse(body) as JsonObject ?? [];

        return (context.Response.StatusCode, document);
    }

    [Fact]
    [Trait("TestCase", "TC-166")]
    [Trait("Threat", "T-09")]
    public void TenantContext_WithNoScope_RefusesToProduceATenantRatherThanDefaultingToAll()
    {
        // The single most dangerous default in a multi-tenant system is "no tenant means every
        // tenant". Asking for the required tenant outside a scope throws, so a repository that forgot
        // to open one fails loudly on the first query instead of quietly returning the whole table.
        AmbientTenantContext context = new();

        Assert.Null(context.TenantId);
        Assert.Throws<TenantContextMissingException>(() => context.RequiredTenantId);
    }

    [Fact]
    [Trait("TestCase", "TC-166")]
    public void TenantContext_ScopesNest_AndRestoreThePreviousTenantExactly()
    {
        // A nested scope that leaked would leave a later query running as the wrong tenant — the worst
        // possible failure mode, because it produces correct-looking data for the wrong customer.
        AmbientTenantContext context = new();

        Guid outer = new("11111111-1111-1111-1111-111111111111");
        Guid inner = new("33333333-3333-3333-3333-333333333333");

        using (context.BeginScope(outer))
        {
            Assert.Equal(outer, context.RequiredTenantId);

            using (context.BeginScope(inner))
            {
                Assert.Equal(inner, context.RequiredTenantId);
            }

            Assert.Equal(outer, context.RequiredTenantId);
        }

        Assert.Null(context.TenantId);
    }

    [Fact]
    [Trait("TestCase", "TC-166")]
    public void TenantContext_RefusesTheEmptyTenant()
    {
        // Guid.Empty is what an uninitialised field looks like, and a scope opened on it would silently
        // become "the tenant whose id is all zeros" — which matches nothing, or worse, matches a row
        // somebody seeded with a default.
        AmbientTenantContext context = new();

        Assert.Throws<ArgumentException>(() => context.BeginScope(Guid.Empty));
    }

    [Fact]
    [Trait("TestCase", "TC-166")]
    [Trait("Threat", "T-09")]
    public async Task ControlPlane_TenantAKeyAgainstEveryResourceOfTenantB_Answers404()
    {
        // The real matrix. It needs two tenants, two credentials and a database enforcing row level
        // security, none of which can be faked without asserting the fake instead of the product.
        //
        // To run it, point the three variables at a deployment seeded with two tenants:
        //   DLE_TEST_CONTROL_URL=https://localhost:8081
        //   DLE_TEST_TENANT_A_KEY=dle_<prefix>_<secret>
        //   DLE_TEST_TENANT_B_RESOURCES=links:728…,domains:5d7…,apps:3b8…,webhooks:8a3…
        string? baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        string? key = Environment.GetEnvironmentVariable(TenantAKeyVariable);
        string? resources = Environment.GetEnvironmentVariable(TenantBResourcesVariable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(baseUrl)
                || string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(resources),
            "Cross-tenant isolation is enforced by PostgreSQL row level security together with the EF "
            + "Core query filters, so it can only be asserted against a real database with two seeded "
            + "tenants. Neither Docker nor PostgreSQL is available on this machine. Set "
            + BaseUrlVariable + ", " + TenantAKeyVariable + " and " + TenantBResourcesVariable
            + " to run it against a deployment.");

        using HttpClient client = new() { BaseAddress = new Uri(baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        List<string> failures = [];

        foreach (string entry in resources!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            string path = "/api/v1/" + parts[0].Trim() + "/" + parts[1].Trim();

            foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Patch, HttpMethod.Delete })
            {
                using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));

                if (method == HttpMethod.Patch)
                {
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    TestContext.Current.CancellationToken);

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    failures.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{method} {path} answered {(int)response.StatusCode}, not 404."));
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Tenant A's credential learned something about tenant B's resources:" + Environment.NewLine
                + string.Join(Environment.NewLine, failures.Select(failure => "  " + failure)));
    }
}
