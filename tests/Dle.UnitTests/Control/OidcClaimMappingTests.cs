using System.Security.Claims;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Verifies that OIDC token claims are correctly mapped to internal DleClaimTypes, and that
/// the mapping fails closed.
/// </summary>
public sealed class OidcClaimMappingTests
{
    private static readonly DleOidcOptions Oidc = new()
    {
        TenantClaim = "dle_tenant",
        RoleClaim = "dle_role",
    };

    [Fact]
    public async Task MapClaimsAsync_MapsTenantAndRoleAndActor()
    {
        Guid tenantId = Guid.NewGuid();
        Guid subId = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        ClaimsPrincipal principal = Principal(
            new Claim("sub", subId.ToString()),
            new Claim("dle_tenant", tenantId.ToString()),
            new Claim("dle_role", "Admin"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(tenantId, "acme", createdAt)),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Equal(tenantId.ToString(), principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Equal(createdAt.ToString("O"), principal.FindFirstValue(DleClaimTypes.TenantCreatedAt));
        Assert.Equal("admin", principal.FindFirstValue(DleClaimTypes.Role));
        Assert.Equal(subId.ToString(), principal.FindFirstValue(DleClaimTypes.ActorId));
        Assert.Equal(DleCaller.UserActor, principal.FindFirstValue(DleClaimTypes.ActorType));

        DleCaller? caller = principal.GetDleCaller();
        Assert.NotNull(caller);
        Assert.Equal(tenantId, caller.TenantId);
        Assert.Equal("admin", caller.Role);
        Assert.Equal(subId, caller.ActorId);
        Assert.Equal(createdAt, caller.TenantCreatedAt);
    }

    [Fact]
    public async Task MapClaimsAsync_ResolvesActiveTenantBySlug()
    {
        Guid tenantId = Guid.NewGuid();

        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", "acme"),
            new Claim("dle_role", "editor"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(tenantId, "acme")),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Equal(tenantId, principal.GetDleCaller()?.TenantId);
    }

    [Fact]
    public async Task MapClaimsAsync_WithoutTenantLookupService_FailsClosed()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("dle_tenant", Guid.NewGuid().ToString()),
            new Claim("dle_role", "owner"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            EmptyServices(),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Null(principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Null(principal.GetDleCaller());
    }

    [Theory]
    [InlineData(TenantStatuses.Suspended)]
    [InlineData(TenantStatuses.Deleted)]
    public async Task MapClaimsAsync_NonActiveTenant_FailsClosed(string status)
    {
        Guid tenantId = Guid.NewGuid();
        Tenant inactive = ActiveTenant(tenantId, "inactive");
        inactive.Status = status;

        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", tenantId.ToString()),
            new Claim("dle_role", "owner"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(inactive),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Null(principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Null(principal.GetDleCaller());
    }

    [Fact]
    public async Task MapClaimsAsync_WithoutTenantClaim_EstablishesNoTenant_EvenWhenAnInstanceTenantIsConfigured()
    {
        Guid instanceTenant = Guid.NewGuid();
        ServiceCollection services = new();
        services.AddSingleton(Options.Create(new DleControlOptions { InstanceTenantId = instanceTenant }));
        services.AddSingleton<ITenantLookup>(new FakeTenantLookup([ActiveTenant(instanceTenant, "ops")]));

        ClaimsPrincipal principal = Principal(
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("dle_role", "owner"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            services.BuildServiceProvider(),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Null(principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Null(principal.GetDleCaller());
    }

    [Fact]
    public async Task MapClaimsAsync_WithoutTenantClaim_UsesTheExplicitDefaultTenant()
    {
        Guid defaultTenant = Guid.NewGuid();
        DleOidcOptions oidc = new()
        {
            TenantClaim = "dle_tenant",
            RoleClaim = "dle_role",
            DefaultTenant = defaultTenant.ToString(),
        };

        ClaimsPrincipal principal = Principal(new Claim("dle_role", "viewer"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(defaultTenant, "default")),
            oidc,
            TestContext.Current.CancellationToken);

        Assert.Equal(defaultTenant, principal.GetDleCaller()?.TenantId);
    }

    [Fact]
    public async Task MapClaimsAsync_TwoDifferentTenantClaims_EstablishNoTenant()
    {
        Guid t1 = Guid.NewGuid();
        Guid t2 = Guid.NewGuid();

        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", t1.ToString()),
            new Claim("dle_tenant", t2.ToString()),
            new Claim("dle_role", "owner"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(t1, "one"), ActiveTenant(t2, "two")),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Null(principal.GetDleCaller());
    }

    [Fact]
    public async Task MapClaimsAsync_DiscardsInternalClaimsTheTokenBrought()
    {
        Guid realTenant = Guid.NewGuid();
        Guid forgedTenant = Guid.NewGuid();

        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", realTenant.ToString()),
            new Claim(DleClaimTypes.TenantId, forgedTenant.ToString()),
            new Claim(DleClaimTypes.Role, "owner"),
            new Claim("dle_role", "viewer"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(realTenant, "real"), ActiveTenant(forgedTenant, "forged")),
            Oidc,
            TestContext.Current.CancellationToken);

        DleCaller? caller = principal.GetDleCaller();
        Assert.NotNull(caller);
        Assert.Equal(realTenant, caller.TenantId);
        Assert.Equal("viewer", caller.Role);
    }

    [Fact]
    public async Task MapClaimsAsync_IgnoresUnknownRolesAndOtherRoleClaims()
    {
        Guid tenantId = Guid.NewGuid();
        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", tenantId.ToString()),
            new Claim("dle_role", "superuser"),
            new Claim("role", "owner"),
            new Claim(ClaimTypes.Role, "owner"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(tenantId, "acme")),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, principal.GetDleCaller()?.Role);
    }

    [Fact]
    public async Task MapClaimsAsync_SeveralKnownRoles_GrantsTheHighest()
    {
        Guid tenantId = Guid.NewGuid();
        ClaimsPrincipal principal = Principal(
            new Claim("dle_tenant", tenantId.ToString()),
            new Claim("dle_role", "viewer"),
            new Claim("dle_role", "editor"));

        await DleOidcClaimMapper.MapClaimsAsync(
            principal,
            ServicesWithTenants(ActiveTenant(tenantId, "acme")),
            Oidc,
            TestContext.Current.CancellationToken);

        Assert.Equal("editor", principal.GetDleCaller()?.Role);
    }

    [Fact]
    public void GetDleCaller_PrincipalNamingTwoTenants_HasNoCaller()
    {
        ClaimsPrincipal principal = new();
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(DleClaimTypes.TenantId, Guid.NewGuid().ToString()), new Claim(DleClaimTypes.Role, "owner")],
            "sdk"));
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(DleClaimTypes.TenantId, Guid.NewGuid().ToString()), new Claim(DleClaimTypes.Role, "owner")],
            "api"));

        Assert.Null(principal.GetDleCaller());
    }

    private static Tenant ActiveTenant(Guid id, string slug, DateTimeOffset? createdAt = null) => new()
    {
        Id = id,
        Slug = slug,
        Name = slug,
        Status = TenantStatuses.Active,
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));

    private static ServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    private static ServiceProvider ServicesWithTenants(params Tenant[] tenants) =>
        new ServiceCollection()
            .AddSingleton<ITenantLookup>(new FakeTenantLookup(tenants))
            .BuildServiceProvider();

    private sealed class FakeTenantLookup(IReadOnlyList<Tenant> tenants) : ITenantLookup
    {
        public Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(tenants.FirstOrDefault(t => t.Id == id));

        public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(tenants.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)));
    }
}
