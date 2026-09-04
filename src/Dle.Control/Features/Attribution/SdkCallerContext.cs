using System.Security.Claims;

using Dle.Control.Identity;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Reads the <see cref="SdkCaller"/> out of the principal the SDK key scheme issued.
/// </summary>
/// <remarks>
/// <para>
/// The SDK endpoints authenticate through <see cref="DleAuthenticationSchemes"/> and are guarded
/// by <see cref="DlePolicies.SdkIngest"/>, which is the only policy that accepts that scheme. By
/// the time a handler runs, the principal has already been verified, rate limited per address and
/// checked against an active tenant; what is left is to read three identifiers out of it.
/// </para>
/// <para>
/// A principal that authenticated but carries no application identifier is refused rather than
/// defaulted. An SDK key is issued for exactly one application, so a missing claim means the
/// credential is not what it says it is, and inventing <see cref="Guid.Empty"/> would file every
/// such installation under one imaginary application (SHARED-KERNEL §17.9).
/// </para>
/// </remarks>
public static class SdkCallerContext
{
    /// <summary>
    /// Reads the caller of an authenticated SDK request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="caller">The tenant, application and credential the request belongs to.</param>
    /// <returns><see langword="true"/> when the principal carries all three identifiers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static bool TryGetSdkCaller(this HttpContext context, out SdkCaller caller)
    {
        ArgumentNullException.ThrowIfNull(context);

        caller = default;

        ClaimsPrincipal principal = context.User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!TryReadGuid(principal, DleClaimTypes.TenantId, out Guid tenantId)
            || !TryReadGuid(principal, DleClaimTypes.AppId, out Guid appId)
            || !TryReadGuid(principal, DleClaimTypes.ActorId, out Guid keyId))
        {
            return false;
        }

        caller = new SdkCaller(tenantId, appId, keyId);
        return true;
    }

    /// <summary>Reads one claim as a non-empty <see cref="Guid"/>.</summary>
    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        string? raw = principal.FindFirstValue(claimType);

        if (!Guid.TryParse(raw, CultureInfo.InvariantCulture, out value) || value == Guid.Empty)
        {
            value = Guid.Empty;
            return false;
        }

        return true;
    }
}
