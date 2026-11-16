using System.Globalization;
using System.Security.Claims;

using Dle.Control.Configuration;
using Dle.Control.Infrastructure;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;

using Microsoft.Extensions.DependencyInjection;

namespace Dle.Control.Identity;

/// <summary>
/// Maps token claims from an external OIDC identity provider to the DLE principal claims (FR-242).
/// </summary>
/// <remarks>
/// <para>
/// Fail closed. Only the two configured claims are read — <see cref="DleOidcOptions.TenantClaim"/> and
/// <see cref="DleOidcOptions.RoleClaim"/> — and any <c>dle:*</c> claim the token itself carries is
/// discarded first, so a provider-side attribute cannot impersonate an internal claim. A token without
/// a tenant establishes no tenant (the request is refused) unless the operator explicitly configured
/// <see cref="DleOidcOptions.DefaultTenant"/>; it never falls back to the operator tenant. A token whose
/// tenant claim names two different tenants establishes none.
/// </para>
/// <para>
/// A role is granted only when the claim holds one of the four known role names. When the provider
/// sends several, the highest known one wins — the provider asserted all of them.
/// </para>
/// </remarks>
public static class DleOidcClaimMapper
{
    /// <summary>
    /// Enriches an authenticated principal with DLE tenant, role, and actor claims.
    /// </summary>
    /// <param name="principal">The incoming principal validated by the JWT bearer handler.</param>
    /// <param name="services">Request service provider for tenant resolution.</param>
    /// <param name="oidc">Configured OIDC options specifying claim names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the claims are mapped.</returns>
    public static async Task MapClaimsAsync(
        ClaimsPrincipal? principal,
        IServiceProvider services,
        DleOidcOptions oidc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(oidc);

        if (principal?.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return;
        }

        // Read provider-asserted values for the configured tenant/role claims and subject before
        // stripping any forged internal `dle:*` claims from the incoming identity.
        List<string> tenantValues = identity.FindAll(oidc.TenantClaim)
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? role = identity.FindAll(oidc.RoleClaim)
            .Select(c => c.Value.Trim())
            .Where(DleRoles.IsKnown)
            .OrderByDescending(DleRoles.Rank)
            .FirstOrDefault();

        Claim? subject = identity.FindFirst("sub")
            ?? identity.FindFirst("oid")
            ?? identity.FindFirst(ClaimTypes.NameIdentifier);

        // Internal claim types are ours to issue. Whatever the token brought under those names is
        // dropped before anything is mapped.
        foreach (Claim foreign in identity.Claims
                     .Where(c => c.Type.StartsWith("dle:", StringComparison.Ordinal))
                     .ToList())
        {
            identity.RemoveClaim(foreign);
        }

        // 1. Tenant (must resolve to an active tenant in storage; fail closed otherwise).
        Tenant? tenant = await ResolveTenantAsync(tenantValues, services, oidc, cancellationToken);

        if (tenant is not null)
        {
            identity.AddClaim(new Claim(DleClaimTypes.TenantId, tenant.Id.ToString()));
            identity.AddClaim(new Claim(
                DleClaimTypes.TenantCreatedAt,
                tenant.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
        }

        // 2. Role: the highest known value of the configured claim.
        if (role is not null)
        {
            identity.AddClaim(new Claim(DleClaimTypes.Role, role.ToLowerInvariant()));
        }

        // 3. Actor (sub / oid), recorded in the audit log when it is a UUID.
        if (subject is not null && Guid.TryParse(subject.Value, CultureInfo.InvariantCulture, out Guid actor))
        {
            identity.AddClaim(new Claim(DleClaimTypes.ActorId, actor.ToString()));
        }

        // 4. Actor type.
        identity.AddClaim(new Claim(DleClaimTypes.ActorType, DleCaller.UserActor));
    }

    /// <summary>Resolves the active tenant a token belongs to, or <see langword="null"/> for none.</summary>
    private static async Task<Tenant?> ResolveTenantAsync(
        List<string> values,
        IServiceProvider services,
        DleOidcOptions oidc,
        CancellationToken cancellationToken)
    {
        if (values.Count > 1)
        {
            // Two tenants in one token is not a choice this code gets to make.
            return null;
        }

        string? reference = values.Count == 1
            ? values[0]
            : string.IsNullOrWhiteSpace(oidc.DefaultTenant) ? null : oidc.DefaultTenant.Trim();

        if (reference is null)
        {
            return null;
        }

        ITenantLookup? tenants = services.GetService<ITenantLookup>()
            ?? services.GetService<TenantRepository>();

        if (tenants is null)
        {
            // Fail closed when tenant storage is not registered so unverified tenant identifiers
            // are never trusted without validation.
            return null;
        }

        Tenant? match = Guid.TryParse(reference, CultureInfo.InvariantCulture, out Guid parsed)
            ? await tenants.FindByIdAsync(parsed, cancellationToken)
            : await tenants.FindBySlugAsync(reference, cancellationToken);

        return IsActive(match) ? match : null;
    }

    /// <summary>
    /// Whether a tenant may be signed in to. The API-key and SDK-key handlers refuse a tenant that is
    /// suspended or deleted; a token from the identity provider has to meet the same rule, or
    /// suspending a tenant would lock out its keys and leave its people signed in.
    /// </summary>
    private static bool IsActive(Tenant? tenant) =>
        tenant is not null && string.Equals(tenant.Status, TenantStatuses.Active, StringComparison.Ordinal);
}
