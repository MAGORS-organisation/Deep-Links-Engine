using System.Text.Json;

using Dle.Domain.Links;
using Dle.Domain.Ports;
using Dle.Domain.Routing;
using Dle.Domain.Serialization;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// The two PostgreSQL types the model leans on that no in-memory provider has: <c>citext</c> and
/// <c>jsonb</c> (§B.5.2).
/// </summary>
/// <remarks>
/// <para>
/// <c>citext</c> is what makes <c>/Promo</c> and <c>/promo</c> the same link and
/// <c>Example.TEST</c> and <c>example.test</c> the same host, without a functional index that would
/// cost the covering resolve index its index-only scan. Its subtlety is that <c>text -&gt; citext</c>
/// is only an assignment cast, so a parameter passed as text compares case sensitively; that is why
/// the hot-path query casts the parameter and why this test compares both ways round.
/// </para>
/// <para>
/// <c>jsonb</c> carries the routing rules, the Open Graph metadata and the UTM defaults. It is
/// asserted in both directions — written by the product's serializer and read back by the store, and
/// written by hand and read back through the snapshot — because a round trip that only ever goes one
/// way through the same code proves nothing about the column.
/// </para>
/// </remarks>
public sealed class CitextAndJsonbTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Citext_SlugLookup_IgnoresCase()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "citext-slug", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("citext"), cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "promo", "https://example.test/", cancellationToken: Ct);

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM links WHERE slug = $1::citext",
                ["PROMO"],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Citext_HostLookup_IgnoresCase()
    {
        string host = TestSeed.UniqueHost("citext-host");

        Guid tenantId = await TestSeed.TenantAsync(Database, "citext-host", cancellationToken: Ct);
        _ = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM domains WHERE host = $1::citext",
                [host.ToUpperInvariant()],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Citext_UniqueIndex_TreatsSlugsDifferingOnlyInCaseAsTheSame()
    {
        // The reason this matters is not convenience: if the index were case sensitive, one tenant
        // could register /Promo beside another product's /promo and the two would resolve to
        // different targets from the same printed URL.
        Guid tenantId = await TestSeed.TenantAsync(Database, "citext-unique", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("citext-uq"), cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "promo", "https://example.test/", cancellationToken: Ct);

        Npgsql.PostgresException failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => TestSeed.LinkAsync(Database, tenantId, domainId, "PROMO", "https://example.test/other", cancellationToken: Ct));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, failure.SqlState);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Jsonb_RoutingRulesAndOgAndUtm_RoundTripThroughTheHotPathStore()
    {
        string host = TestSeed.UniqueHost("jsonb");

        Guid tenantId = await TestSeed.TenantAsync(Database, "jsonb", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        string rules = TestRules.IosAppOrStore(
            "https://apps.apple.com/app/id123456789?pt=1234&ct=autumn",
            "/product/42");

        OgMeta og = new()
        {
            Title = "Autumn sale",
            Description = "Everything, half price",
            ImageUrl = "https://cdn.example.test/autumn.png",
            SiteName = "Example",
        };

        Dictionary<string, string> utm = new(StringComparer.Ordinal)
        {
            ["utm_source"] = "newsletter",
            ["utm_medium"] = "email",
        };

        long linkId = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "autumn",
            "https://example.test/autumn",
            rules,
            JsonSerializer.Serialize(og, DleDomainJsonContext.Default.OgMeta),
            JsonSerializer.Serialize(utm, DleDomainJsonContext.Default.DictionaryStringString),
            cancellationToken: Ct);

        // Read back through the store the resolve path actually uses, not through a second copy of
        // the query written for the test.
        await using EdgeStores stores = EdgeStores.Open(Database);

        LinkSnapshot? snapshot = await stores.Links.FindAsync(host, "autumn", Ct);

        Assert.NotNull(snapshot);
        Assert.Equal(linkId, snapshot.Id);
        Assert.Equal(tenantId, snapshot.TenantId);

        Assert.Equal(2, snapshot.RoutingRules.Count);
        Assert.Equal("ios", snapshot.RoutingRules[0].Id);
        Assert.Equal(RoutingActionKind.AppOrStore, snapshot.RoutingRules[0].Then.Action);
        Assert.Equal("/product/42", snapshot.RoutingRules[0].Then.DeeplinkPath);
        Assert.Equal("default", snapshot.RoutingRules[1].Id);

        Assert.Equal("Autumn sale", snapshot.Og.Title);
        Assert.Equal("https://cdn.example.test/autumn.png", snapshot.Og.ImageUrl);

        Assert.Equal("newsletter", snapshot.Utm["utm_source"]);
        Assert.Equal("email", snapshot.Utm["utm_medium"]);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Jsonb_ColumnsAreQueryableAsJsonRatherThanAsText()
    {
        // If these columns had been created as text the product would still work and this suite
        // would still pass — right up to the first report that filters on a rule's action.
        Guid tenantId = await TestSeed.TenantAsync(Database, "jsonb-type", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("jsonb-type"), cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(
            Database,
            tenantId,
            domainId,
            "typed",
            "https://example.test/",
            TestRules.IosAppOrStore("https://apps.apple.com/app/id1"),
            cancellationToken: Ct);

        Assert.Equal(
            "ios",
            await Sql.ScalarAsync<string>(
                Database.DataSource,
                "SELECT routing_rules -> 0 ->> 'id' FROM links WHERE slug = 'typed'::citext",
                cancellationToken: Ct));

        IReadOnlyList<string> jsonbColumns = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT c.relname || '.' || a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relname = 'links'
              AND a.attname IN ('routing_rules', 'og_meta', 'utm')
              AND format_type(a.atttypid, a.atttypmod) = 'jsonb'
            ORDER BY a.attname
            """,
            cancellationToken: Ct);

        Assert.Equal(["links.og_meta", "links.routing_rules", "links.utm"], jsonbColumns);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task DomainConfig_ReadsBrandingAndDefaultOgOutOfJsonb()
    {
        string host = TestSeed.UniqueHost("branding");

        Guid tenantId = await TestSeed.TenantAsync(Database, "branding", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            """
            UPDATE domains
            SET branding = $2::jsonb,
                default_og = $3::jsonb
            WHERE id = $1
            """,
            [
                domainId,
                """{"default_language":"sk","product_name":"Example"}""",
                """{"site_name":"Example","type":"website"}""",
            ],
            Ct);

        await using EdgeStores stores = EdgeStores.Open(Database);

        DomainRuntimeConfig? config = await stores.Domains.GetDomainAsync(host, Ct);

        Assert.NotNull(config);
        Assert.Equal(tenantId, config.TenantId);
        Assert.Equal(host, config.Host);
        Assert.Equal("sk", config.DefaultLanguage);
        Assert.NotNull(config.DefaultOg);
        Assert.Equal("Example", config.DefaultOg.SiteName);
    }
}
