using Npgsql;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// The four uniqueness rules §B.5.2 and §B.5.3 put in the schema rather than in a service.
/// </summary>
/// <remarks>
/// Every one of them protects an invariant that a race would otherwise break: two requests creating
/// the same slug, two installations of the same application reporting the same identifier, two
/// attributions for one installation, and — the interesting one — two installations both claiming
/// the same click. A check in application code cannot enforce any of these under concurrency, which
/// is why they are indexes, and why an in-memory provider would not test them at all.
/// </remarks>
public sealed class UniqueIndexTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Links_SameSlugOnSameDomain_IsRejectedByTheUniqueIndex()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "slug-unique", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("slug"), cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, domainId, "promo", "https://example.test/a", cancellationToken: Ct);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => TestSeed.LinkAsync(Database, tenantId, domainId, "promo", "https://example.test/b", cancellationToken: Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("uq_links_domain_slug", failure.ConstraintName);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Links_SameSlugOnDifferentDomains_AreBothAccepted()
    {
        // The slug space is per domain, not per instance. Two customers each owning "summer" on
        // their own host is the normal case, and a unique index on slug alone would forbid it.
        Guid tenantId = await TestSeed.TenantAsync(Database, "slug-per-domain", cancellationToken: Ct);
        Guid first = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("dom-a"), cancellationToken: Ct);
        Guid second = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("dom-b"), cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Database, tenantId, first, "summer", "https://example.test/a", cancellationToken: Ct);
        _ = await TestSeed.LinkAsync(Database, tenantId, second, "summer", "https://example.test/b", cancellationToken: Ct);

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM links WHERE slug = 'summer'", cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.3")]
    public async Task Installs_SameInstallIdForOneApp_IsRejectedByTheUniqueIndex()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "install-unique", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, tenantId, domainId: null, "android", "test.app", cancellationToken: Ct);

        _ = await TestSeed.InstallAsync(Database, tenantId, appId, "install-1", cancellationToken: Ct);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => TestSeed.InstallAsync(Database, tenantId, appId, "install-1", cancellationToken: Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("uq_installs_app_install", failure.ConstraintName);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-143")]
    [Trait("Spec", "B.5.3")]
    public async Task Attributions_SecondAttributionForOneInstall_IsRejected()
    {
        // One installation, one attribution: the rule that makes a repeated /v1/resolve idempotent
        // rather than a second credit (TC-143).
        Guid tenantId = await TestSeed.TenantAsync(Database, "attr-install", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, tenantId, domainId: null, "android", "test.app", cancellationToken: Ct);
        Guid installRowId = await TestSeed.InstallAsync(Database, tenantId, appId, "install-1", cancellationToken: Ct);

        _ = await TestSeed.AttributionAsync(Database, tenantId, installRowId, "click-a", linkId: null, cancellationToken: Ct);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => TestSeed.AttributionAsync(Database, tenantId, installRowId, "click-b", linkId: null, cancellationToken: Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("uq_attributions_install", failure.ConstraintName);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-144")]
    [Trait("Spec", "B.5.3")]
    public async Task Attributions_SecondInstallClaimingTheSameClick_IsRejected()
    {
        // TC-144: one click is worth at most one install. There is no foreign key to click_events —
        // a partitioned table has a composite primary key and nothing to reference — so this partial
        // unique index is the only thing standing between the product and the cheapest attribution
        // fraud there is.
        Guid tenantId = await TestSeed.TenantAsync(Database, "attr-click", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, tenantId, domainId: null, "android", "test.app", cancellationToken: Ct);

        Guid firstInstall = await TestSeed.InstallAsync(Database, tenantId, appId, "install-1", cancellationToken: Ct);
        Guid secondInstall = await TestSeed.InstallAsync(Database, tenantId, appId, "install-2", cancellationToken: Ct);

        _ = await TestSeed.AttributionAsync(Database, tenantId, firstInstall, "shared-click", linkId: null, cancellationToken: Ct);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => TestSeed.AttributionAsync(Database, tenantId, secondInstall, "shared-click", linkId: null, cancellationToken: Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("uq_attributions_click", failure.ConstraintName);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.3")]
    public async Task Attributions_ManyRowsWithoutAClickId_AreAllAccepted()
    {
        // The index is partial for a reason. An organic install and a direct open both carry no
        // click identifier, and a plain unique index would let the instance hold exactly one of
        // them (MatchTypeNames.None, MatchTypeNames.DirectOpen).
        Guid tenantId = await TestSeed.TenantAsync(Database, "attr-null-click", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, tenantId, domainId: null, "android", "test.app", cancellationToken: Ct);

        Guid firstInstall = await TestSeed.InstallAsync(Database, tenantId, appId, "install-1", cancellationToken: Ct);
        Guid secondInstall = await TestSeed.InstallAsync(Database, tenantId, appId, "install-2", cancellationToken: Ct);

        _ = await TestSeed.AttributionAsync(Database, tenantId, firstInstall, clickId: null, linkId: null, "none", 0m, Ct);
        _ = await TestSeed.AttributionAsync(Database, tenantId, secondInstall, clickId: null, linkId: null, "direct_open", 1.00m, Ct);

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM attributions WHERE click_id IS NULL",
                cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.5.2")]
    public async Task Domains_SameHostTwice_IsRejectedByTheUniqueIndex()
    {
        // A host names exactly one tenant's link space. Two rows for one host would make the
        // resolve query's answer depend on which one the planner happened to return.
        Guid firstTenant = await TestSeed.TenantAsync(Database, "host-a", cancellationToken: Ct);
        Guid secondTenant = await TestSeed.TenantAsync(Database, "host-b", cancellationToken: Ct);

        string host = TestSeed.UniqueHost("shared");

        _ = await TestSeed.DomainAsync(Database, firstTenant, host, cancellationToken: Ct);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => TestSeed.DomainAsync(Database, secondTenant, host, cancellationToken: Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("uq_domains_host", failure.ConstraintName);
    }
}
