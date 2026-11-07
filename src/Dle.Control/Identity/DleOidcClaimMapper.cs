using System.Globalization;
using System.Security.Claims;
using Dle.Control.Configuration;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// Maps token claims from external OIDC identity providers to internal DLE principal claims (FR-242).
/// </summary>
public static class DleOidcClaimMapper
{
    /// <summary>
    /// Enriches an authenticated principal with DLE tenant, role, and actor claims.
    /// </summary>
    /// <param name="principal">The incoming principal validated by the JWT bearer handler.</param>
    /// <param name="services">Request service provider for tenant resolution.</param>
    /// <param name="oidc">Configured OIDC options specifying claim names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task MapClaimsAsync(
        ClaimsPrincipal? principal,
        IServiceProvider services,
        DleOidcOptions oidc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oidc);

        if (principal?.Identity is not ClaimsIdentity identityClaim || !identityClaim.IsAuthenticated)
        {
            return;
        }

        // 1. Map Tenant Claim
        string configuredTenantClaim = oidc.TenantClaim;
        Claim? tenantClaim = principal.FindFirst(configuredTenantClaim)
            ?? principal.FindFirst(DleClaimTypes.TenantId);

        Guid resolvedTenantId = Guid.Empty;

        if (tenantClaim is not null)
        {
            if (Guid.TryParse(tenantClaim.Value, CultureInfo.InvariantCulture, out Guid parsedId)
                && parsedId != Guid.Empty)
            {
                resolvedTenantId = parsedId;
            }
            else if (!string.IsNullOrWhiteSpace(tenantClaim.Value))
            {
                // Slug-based tenant resolution
                TenantRepository? tenantRepo = services.GetService<TenantRepository>();
                if (tenantRepo is not null)
                {
                    Tenant? matchedTenant = await tenantRepo.FindBySlugAsync(tenantClaim.Value.Trim(), cancellationToken);
                    if (matchedTenant is not null)
                    {
                        resolvedTenantId = matchedTenant.Id;
                    }
                }
            }
        }

        // Fallback to InstanceTenantId if unmapped and InstanceTenantId is configured
        if (resolvedTenantId == Guid.Empty)
        {
            IOptions<DleControlOptions>? controlOpts = services.GetService<IOptions<DleControlOptions>>();
            if (controlOpts?.Value.InstanceTenantId is Guid instanceTenant && instanceTenant != Guid.Empty)
            {
                resolvedTenantId = instanceTenant;
            }
        }

        if (resolvedTenantId != Guid.Empty && !identityClaim.HasClaim(c => c.Type == DleClaimTypes.TenantId))
        {
            identityClaim.AddClaim(new Claim(DleClaimTypes.TenantId, resolvedTenantId.ToString()));
        }

        // 2. Map Role Claim
        string configuredRoleClaim = oidc.RoleClaim;
        Claim? roleClaim = principal.FindFirst(configuredRoleClaim)
            ?? principal.FindFirst(ClaimTypes.Role)
            ?? principal.FindFirst("role")
            ?? principal.FindFirst(DleClaimTypes.Role);

        if (roleClaim is not null && !identityClaim.HasClaim(c => c.Type == DleClaimTypes.Role))
        {
            identityClaim.AddClaim(new Claim(DleClaimTypes.Role, roleClaim.Value.ToLowerInvariant()));
        }

        // 3. Map Actor Claim (sub / oid / uid)
        Claim? subClaim = principal.FindFirst("sub")
            ?? principal.FindFirst("oid")
            ?? principal.FindFirst(ClaimTypes.NameIdentifier);

        if (subClaim is not null && !identityClaim.HasClaim(c => c.Type == DleClaimTypes.ActorId))
        {
            if (Guid.TryParse(subClaim.Value, out Guid subGuid))
            {
                identityClaim.AddClaim(new Claim(DleClaimTypes.ActorId, subGuid.ToString()));
            }
        }

        // 4. Ensure ActorType is UserActor
        if (!identityClaim.HasClaim(c => c.Type == DleClaimTypes.ActorType))
        {
            identityClaim.AddClaim(new Claim(DleClaimTypes.ActorType, DleCaller.UserActor));
        }
    }
}
