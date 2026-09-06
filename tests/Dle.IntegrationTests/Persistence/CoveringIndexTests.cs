namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// The planner note of §B.5.2: <c>ix_links_resolve</c> has to be answered by an index-only scan, and
/// that has to be verified with <c>EXPLAIN (ANALYZE, BUFFERS)</c> rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the product where that claim can be checked, and §B.5.2 is explicit
/// that the latency budget of §B.6.1 does not hold without it. Two things can break it
/// independently. The index can stop covering the query — every column the query projects has to be
/// a key or an included column — and the visibility map can go stale, which sends an otherwise
/// index-only scan back to the heap; the migration lowers <c>autovacuum_vacuum_scale_factor</c> to
/// 0.02 precisely to keep the second from happening.
/// </para>
/// <para>
/// The table is seeded with enough rows for the planner to have a real choice. Against a handful of
/// rows PostgreSQL prefers a sequential scan whatever indexes exist, and a test that measured that
/// would be measuring its own fixture.
/// </para>
/// </remarks>
public sealed class CoveringIndexTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    /// <summary>How many links are seeded before the planner is asked to choose.</summary>
    private const int SeededLinks = 4_000;

    /// <summary>
    /// The projection of <c>DapperLinkStore</c>, copied verbatim.
    /// </summary>
    /// <remarks>
    /// A copy, because the statement is a private constant of the store and there is no seam that
    /// exposes it. It has to be kept in step with
    /// <c>Dle.Persistence.Fast.Links.DapperLinkStore.Sql</c>; if the two drift, this test stops
    /// measuring the query that actually runs, which is worse than not having it.
    /// </remarks>
    private const string ResolveQuery = """
        SELECT l.id,
               l.tenant_id,
               l.domain_id,
               l.slug,
               l.target_url,
               l.deeplink_path,
               l.routing_rules,
               l.og_meta,
               l.utm,
               l.title,
               l.campaign_id,
               l.is_active,
               l.starts_at,
               l.expires_at,
               l.expired_url,
               l.quarantined_at,
               t.consent_mode                AS tenant_consent_mode,
               d.consent_mode_override       AS domain_consent_mode,
               ios.custom_scheme             AS ios_custom_scheme,
               ios.store_url                 AS ios_store_url,
               android.custom_scheme         AS android_custom_scheme,
               android.store_url             AS android_store_url
        FROM links l
        JOIN domains d ON d.id = l.domain_id
        JOIN tenants t ON t.id = l.tenant_id
        LEFT JOIN LATERAL (
            SELECT a.custom_scheme, a.store_url
            FROM apps a
            JOIN app_domains ad ON ad.app_id = a.id
            WHERE ad.domain_id = l.domain_id AND lower(a.platform) = 'ios'
            ORDER BY a.created_at
            LIMIT 1
        ) ios ON true
        LEFT JOIN LATERAL (
            SELECT a.custom_scheme, a.store_url
            FROM apps a
            JOIN app_domains ad ON ad.app_id = a.id
            WHERE ad.domain_id = l.domain_id AND lower(a.platform) = 'android'
            ORDER BY a.created_at
            LIMIT 1
        ) android ON true
        WHERE d.host = $1::citext
          AND l.slug = $2::citext
          AND d.is_active
        """;

    /// <summary>
    /// The columns §B.5.2 puts in the index, projected on their own. This is the shape the index was
    /// designed to cover.
    /// </summary>
    private const string CoveredProjection = """
        SELECT l.target_url,
               l.deeplink_path,
               l.routing_rules,
               l.og_meta,
               l.is_active,
               l.starts_at,
               l.expires_at,
               l.quarantined_at,
               l.tenant_id
        FROM links l
        WHERE l.domain_id = $1 AND l.slug = $2::citext
        """;

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task ResolveIndex_ForTheColumnsItCovers_IsAnsweredByAnIndexOnlyScan()
    {
        Seeded seeded = await SeedAsync();

        string plan = await Sql.ExplainAsync(
            Database.DataSource,
            CoveredProjection,
            "ANALYZE, BUFFERS",
            [seeded.DomainId, "link-1000"],
            Ct);

        Assert.Contains("Index Only Scan", plan, StringComparison.Ordinal);
        Assert.Contains("ix_links_resolve", plan, StringComparison.Ordinal);

        // A fresh visibility map is the other half of the claim: with a stale one the scan still
        // says "Index Only Scan" and then reports heap fetches, and the latency budget is gone.
        Assert.Contains("Heap Fetches: 0", plan, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task ResolveQuery_AsTheEdgeIssuesIt_IsAnsweredByAnIndexOnlyScanOnIxLinksResolve()
    {
        // §B.5.2: "over cez EXPLAIN (ANALYZE, BUFFERS), že sa index-only scan skutočne používa —
        // inak latenčný rozpočet z §B.6.1 neplatí." This asserts the specified behaviour against the
        // statement the resolve path actually runs, not against a reduced one.
        Seeded seeded = await SeedAsync();

        string plan = await Sql.ExplainAsync(
            Database.DataSource,
            ResolveQuery,
            "ANALYZE, BUFFERS",
            [seeded.Host, "link-1000"],
            Ct);

        Assert.True(
            plan.Contains("Index Only Scan", StringComparison.Ordinal)
            && plan.Contains("ix_links_resolve", StringComparison.Ordinal),
            "The resolve query was not answered by an index-only scan on ix_links_resolve. §B.5.2 "
            + "says the latency budget of §B.6.1 does not hold without one. The plan was:\n" + plan);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task ResolveIndex_IsPreferredOverASequentialScanOnceTheTableIsRealisticallySized()
    {
        // The weaker claim, and the one that is worth having even if the covering scan is not
        // reached: whatever else the planner does, it must not read the whole table to answer one
        // short link.
        Seeded seeded = await SeedAsync();

        string plan = await Sql.ExplainAsync(
            Database.DataSource,
            ResolveQuery,
            "ANALYZE, BUFFERS",
            [seeded.Host, "link-1000"],
            Ct);

        Assert.DoesNotContain("Seq Scan on links", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seeds one tenant, one domain and <see cref="SeededLinks"/> links, then vacuums and analyses
    /// so that both the statistics and the visibility map are current.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        string host = TestSeed.UniqueHost("covering");

        Guid tenantId = await TestSeed.TenantAsync(Database, "covering", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        // generate_series rather than a loop of round trips: four thousand inserts one at a time is
        // most of the runtime of this class for no benefit.
        _ = await Sql.ExecuteAsync(
            Database.DataSource,
            """
            INSERT INTO links (id, tenant_id, domain_id, slug, target_url, routing_rules)
            SELECT $1::bigint + g,
                   $2,
                   $3,
                   'link-' || g,
                   'https://example.test/' || g,
                   $4::jsonb
            FROM generate_series(1, $5::int) AS g
            """,
            [TestSeed.NextLinkId(), tenantId, domainId, TestRules.WebDefault(), SeededLinks],
            Ct);

        // VACUUM sets the all-visible bits an index-only scan needs; ANALYZE gives the planner the
        // statistics to prefer the index at all. Autovacuum would get there eventually, and
        // "eventually" is not a thing a test can wait for.
        _ = await Sql.ExecuteAsync(Database.DataSource, "VACUUM (ANALYZE) links", cancellationToken: Ct);
        _ = await Sql.ExecuteAsync(Database.DataSource, "ANALYZE domains, tenants, apps, app_domains", cancellationToken: Ct);

        Assert.Equal(
            (long)SeededLinks,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM links WHERE domain_id = $1",
                [domainId],
                Ct));

        return new Seeded(tenantId, domainId, host);
    }

    /// <summary>What the seed produced.</summary>
    private sealed record Seeded(Guid TenantId, Guid DomainId, string Host);
}
