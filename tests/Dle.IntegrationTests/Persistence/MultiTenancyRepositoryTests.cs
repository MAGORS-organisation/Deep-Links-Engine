using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// Tenant isolation at the repository level, with two tenants seeded and neither able to see the
/// other (T-09, S-02, FR-241).
/// </summary>
/// <remarks>
/// <para>
/// The point of these tests is that no handler compares a tenant identifier. Isolation is a named
/// query filter attached to every tenant-owned entity type in the model, which means it holds for
/// queries nobody has written yet — and it means a deliberately crafted foreign identifier is
/// indistinguishable from one that never existed, which is the whole of TC-166.
/// </para>
/// <para>
/// Two tenants are seeded, one row of each kind is created for each, and every read is then made in
/// tenant A's scope. Anything that comes back belonging to B is a leak.
/// </para>
/// </remarks>
public sealed class MultiTenancyRepositoryTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task Links_ReadInOneTenantsScope_NeverReturnAnotherTenantsRows()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, seeded.TenantA);
        LinkRepository links = scope.Resolve<LinkRepository>();

        Assert.NotNull(await links.GetAsync(seeded.LinkA, includeQuarantined: false, Ct));

        // The crafted foreign identifier: it exists, it is valid, and it is not this tenant's.
        Assert.Null(await links.GetAsync(seeded.LinkB, includeQuarantined: false, Ct));
        Assert.Null(await links.GetAsync(seeded.LinkB, includeQuarantined: true, Ct));

        Assert.Null(await links.FindBySlugAsync(seeded.DomainB, "b-link", includeQuarantined: false, Ct));

        PagedResponse<Link> page = await links.ListAsync(new LinkListQuery { Limit = 50 }, Ct);

        Assert.All(page.Items, link => Assert.Equal(seeded.TenantA, link.TenantId));
        Assert.Single(page.Items);
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task Domains_ReadInOneTenantsScope_NeverReturnAnotherTenantsRows()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, seeded.TenantA);
        DomainRepository domains = scope.Resolve<DomainRepository>();

        Assert.NotNull(await domains.GetAsync(seeded.DomainA, Ct));
        Assert.Null(await domains.GetAsync(seeded.DomainB, Ct));
        Assert.Null(await domains.FindByHostAsync(seeded.HostB, Ct));

        IReadOnlyList<LinkDomain> all = await domains.ListAsync(Ct);

        Assert.All(all, domain => Assert.Equal(seeded.TenantA, domain.TenantId));
        Assert.Single(all);
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task Apps_ReadInOneTenantsScope_NeverReturnAnotherTenantsRows()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, seeded.TenantA);
        AppRepository apps = scope.Resolve<AppRepository>();

        Assert.NotNull(await apps.GetAsync(seeded.AppA, Ct));
        Assert.Null(await apps.GetAsync(seeded.AppB, Ct));

        IReadOnlyList<App> all = await apps.ListAsync(Ct);

        Assert.All(all, app => Assert.Equal(seeded.TenantA, app.TenantId));
        Assert.Single(all);
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task Attributions_ReadInOneTenantsScope_NeverReturnAnotherTenantsRows()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, seeded.TenantA);
        AttributionRepository attributions = scope.Resolve<AttributionRepository>();

        Assert.NotNull(await attributions.FindByInstallAsync(seeded.InstallA, Ct));
        Assert.Null(await attributions.FindByInstallAsync(seeded.InstallB, Ct));

        // The click identifier is public: it travels in a store referrer where anybody can read it.
        // Looking one up from the wrong tenant has to be indistinguishable from looking up a click
        // that never happened.
        Assert.NotNull(await attributions.FindByClickIdAsync("click-a", Ct));
        Assert.Null(await attributions.FindByClickIdAsync("click-b", Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task Tenants_ReadInOneTenantsScope_ReturnOnlyThatTenant()
    {
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, seeded.TenantA);
        TenantRepository tenants = scope.Resolve<TenantRepository>();

        Assert.NotNull(await tenants.GetAsync(seeded.TenantA, Ct));
        Assert.Null(await tenants.GetAsync(seeded.TenantB, Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task AnyQuery_MadeWithNoTenantInScope_FailsRatherThanReturningEverything()
    {
        // The filter reads ITenantContext.RequiredTenantId, which throws when nothing established a
        // tenant. That is the difference between a forgotten scope being an exception and a
        // forgotten scope being a data breach.
        Seeded seeded = await SeedTwoTenantsAsync();
        Assert.NotEqual(Guid.Empty, seeded.TenantA);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database);
        LinkRepository links = scope.Resolve<LinkRepository>();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => links.ListAsync(new LinkListQuery { Limit = 50 }, Ct));

        // Anywhere in the chain: the provider is free to wrap what the filter's parameter threw, and
        // what matters is that the query refused rather than answered.
        Assert.True(
            Chain(failure).OfType<TenantContextMissingException>().Any(),
            "A query issued outside a tenant scope has to fail with TenantContextMissingException. "
            + "It failed with: " + failure);
    }

    /// <summary>An exception and everything it wraps.</summary>
    /// <param name="exception">The outermost exception.</param>
    /// <returns>The chain, outermost first.</returns>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "T-09")]
    public async Task CrossTenantScope_WithoutDroppingTheFilterByName_StillReturnsNothing()
    {
        // Opening the window is not the same as widening the query. A query that forgets to call
        // AcrossTenants sees Guid.Empty, which no row carries, so it fails closed.
        Seeded seeded = await SeedTwoTenantsAsync();

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database);

        using CrossTenantScope _ = scope.Db.BeginCrossTenantScope("the test asserts the fail-closed behaviour");

        LinkRepository links = scope.Resolve<LinkRepository>();

        Assert.Null(await links.GetAsync(seeded.LinkA, includeQuarantined: false, Ct));
        Assert.Null(await links.GetAsync(seeded.LinkB, includeQuarantined: false, Ct));
    }

    /// <summary>Seeds two complete tenants, each with a domain, an app, a link and an attribution.</summary>
    private async Task<Seeded> SeedTwoTenantsAsync()
    {
        string hostA = TestSeed.UniqueHost("tenant-a");
        string hostB = TestSeed.UniqueHost("tenant-b");

        Guid tenantA = await TestSeed.TenantAsync(Database, "tenant-a", cancellationToken: Ct);
        Guid tenantB = await TestSeed.TenantAsync(Database, "tenant-b", cancellationToken: Ct);

        Guid domainA = await TestSeed.DomainAsync(Database, tenantA, hostA, cancellationToken: Ct);
        Guid domainB = await TestSeed.DomainAsync(Database, tenantB, hostB, cancellationToken: Ct);

        Guid appA = await TestSeed.AppAsync(Database, tenantA, domainA, "ios", "test.a", "TEAMA", cancellationToken: Ct);
        Guid appB = await TestSeed.AppAsync(Database, tenantB, domainB, "ios", "test.b", "TEAMB", cancellationToken: Ct);

        long linkA = await TestSeed.LinkAsync(Database, tenantA, domainA, "a-link", "https://a.example.test/", cancellationToken: Ct);
        long linkB = await TestSeed.LinkAsync(Database, tenantB, domainB, "b-link", "https://b.example.test/", cancellationToken: Ct);

        Guid installA = await TestSeed.InstallAsync(Database, tenantA, appA, "install-a", cancellationToken: Ct);
        Guid installB = await TestSeed.InstallAsync(Database, tenantB, appB, "install-b", cancellationToken: Ct);

        _ = await TestSeed.AttributionAsync(Database, tenantA, installA, "click-a", linkA, cancellationToken: Ct);
        _ = await TestSeed.AttributionAsync(Database, tenantB, installB, "click-b", linkB, cancellationToken: Ct);

        return new Seeded(tenantA, tenantB, domainA, domainB, hostA, hostB, appA, appB, linkA, linkB, installA, installB);
    }

    /// <summary>Identifiers of the two seeded tenants and everything they own.</summary>
    private sealed record Seeded(
        Guid TenantA,
        Guid TenantB,
        Guid DomainA,
        Guid DomainB,
        string HostA,
        string HostB,
        Guid AppA,
        Guid AppB,
        long LinkA,
        long LinkB,
        Guid InstallA,
        Guid InstallB);
}
