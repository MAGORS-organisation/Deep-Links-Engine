using System.Security.Claims;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Verifies that OIDC token claims are correctly mapped to internal DleClaimTypes (FR-242).
/// </summary>
public sealed class OidcClaimMappingTests
{
    [Fact]
    public async Task MapClaimsAsync_MapsTenantAndRoleAndActor()
    {
        Guid tenantId = Guid.NewGuid();
        Guid subId = Guid.NewGuid();

        ClaimsIdentity identity = new("TestAuth");
        identity.AddClaim(new Claim("sub", subId.ToString()));
        identity.AddClaim(new Claim("dle_tenant", tenantId.ToString()));
        identity.AddClaim(new Claim("dle_role", "admin"));

        ClaimsPrincipal principal = new(identity);

        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();

        DleOidcOptions oidc = new()
        {
            TenantClaim = "dle_tenant",
            RoleClaim = "dle_role",
        };

        await DleOidcClaimMapper.MapClaimsAsync(principal, provider, oidc, TestContext.Current.CancellationToken);

        Assert.Equal(tenantId.ToString(), principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Equal("admin", principal.FindFirstValue(DleClaimTypes.Role));
        Assert.Equal(subId.ToString(), principal.FindFirstValue(DleClaimTypes.ActorId));
        Assert.Equal(DleCaller.UserActor, principal.FindFirstValue(DleClaimTypes.ActorType));

        DleCaller? caller = principal.GetDleCaller();
        Assert.NotNull(caller);
        Assert.Equal(tenantId, caller.TenantId);
        Assert.Equal("admin", caller.Role);
        Assert.Equal(subId, caller.ActorId);
    }

    [Fact]
    public async Task MapClaimsAsync_FallbacksToInstanceTenantId_WhenTenantClaimMissing()
    {
        Guid instanceTenantId = Guid.NewGuid();
        Guid subId = Guid.NewGuid();

        ClaimsIdentity identity = new("TestAuth");
        identity.AddClaim(new Claim("sub", subId.ToString()));
        identity.AddClaim(new Claim("dle_role", "owner"));

        ClaimsPrincipal principal = new(identity);

        ServiceCollection services = new();
        services.AddSingleton(Options.Create(new DleControlOptions { InstanceTenantId = instanceTenantId }));
        ServiceProvider provider = services.BuildServiceProvider();

        DleOidcOptions oidc = new()
        {
            TenantClaim = "dle_tenant",
            RoleClaim = "dle_role",
        };

        await DleOidcClaimMapper.MapClaimsAsync(principal, provider, oidc, TestContext.Current.CancellationToken);

        Assert.Equal(instanceTenantId.ToString(), principal.FindFirstValue(DleClaimTypes.TenantId));
        Assert.Equal("owner", principal.FindFirstValue(DleClaimTypes.Role));

        DleCaller? caller = principal.GetDleCaller();
        Assert.NotNull(caller);
        Assert.Equal(instanceTenantId, caller.TenantId);
    }
}
