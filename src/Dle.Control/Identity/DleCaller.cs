using System.Security.Claims;

namespace Dle.Control.Identity;

/// <summary>
/// The authenticated caller, read from the claims the authentication handlers produced.
/// </summary>
/// <param name="TenantId">Tenant every query and write in this request is scoped to (FR-241).</param>
/// <param name="ActorId">Identifier of the credential or the user.</param>
/// <param name="ActorType">Kind of actor, as recorded in the audit trail.</param>
/// <param name="Role">The role from <see cref="DleRoles"/>.</param>
/// <param name="Scopes">Granted scopes; empty means the role alone governs.</param>
/// <param name="KeyPrefix">Non-secret prefix of the presented key, safe to log.</param>
/// <param name="TenantCreatedAt">When the tenant was provisioned, for the §E.9 new-tenant limit.</param>
public sealed record DleCaller(
    Guid TenantId,
    Guid? ActorId,
    string ActorType,
    string Role,
    IReadOnlyList<string> Scopes,
    string? KeyPrefix,
    DateTimeOffset? TenantCreatedAt)
{
    /// <summary>Actor type recorded for a control-plane API key.</summary>
    public const string ApiKeyActor = "api_key";

    /// <summary>Actor type recorded for a key embedded in a customer application.</summary>
    public const string SdkKeyActor = "sdk_key";

    /// <summary>Actor type recorded for a person signed in through the identity provider.</summary>
    public const string UserActor = "user";

    /// <summary>Whether the caller holds a scope, treating an empty scope list as role-governed.</summary>
    /// <param name="scope">The scope to look for.</param>
    /// <returns><see langword="true"/> when the operation is within the caller's scopes.</returns>
    public bool HasScope(string scope)
    {
        if (Scopes.Count == 0)
        {
            return true;
        }

        foreach (string granted in Scopes)
        {
            if (string.Equals(granted, scope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Reads a <see cref="DleCaller"/> out of a principal.
/// </summary>
public static class DleCallerExtensions
{
    /// <summary>
    /// Reads the caller, or <see langword="null"/> when the principal carries no usable tenant.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The caller, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A principal without a parseable tenant claim yields <see langword="null"/> rather than a
    /// caller scoped to <see cref="Guid.Empty"/>. Everything downstream then refuses the request,
    /// which is the required behaviour: there is no such thing as a request that touches tenant data
    /// without a tenant (FR-241, T-09).
    /// </remarks>
    public static DleCaller? GetDleCaller(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? tenantValue = principal.FindFirstValue(DleClaimTypes.TenantId);

        if (!Guid.TryParse(tenantValue, CultureInfo.InvariantCulture, out Guid tenantId)
            || tenantId == Guid.Empty)
        {
            return null;
        }

        Guid? actorId = Guid.TryParse(
            principal.FindFirstValue(DleClaimTypes.ActorId),
            CultureInfo.InvariantCulture,
            out Guid parsedActor)
            ? parsedActor
            : null;

        DateTimeOffset? tenantCreatedAt = DateTimeOffset.TryParse(
            principal.FindFirstValue(DleClaimTypes.TenantCreatedAt),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsedCreatedAt)
            ? parsedCreatedAt
            : null;

        List<string> scopes = [];

        foreach (Claim claim in principal.FindAll(DleClaimTypes.Scope))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                scopes.Add(claim.Value);
            }
        }

        return new DleCaller(
            tenantId,
            actorId,
            principal.FindFirstValue(DleClaimTypes.ActorType) ?? DleCaller.UserActor,
            principal.FindFirstValue(DleClaimTypes.Role) ?? string.Empty,
            scopes,
            principal.FindFirstValue(DleClaimTypes.KeyPrefix),
            tenantCreatedAt);
    }

    /// <summary>
    /// Reads the caller of the current request, throwing when there is none.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>The caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// The request reached a handler without an authenticated tenant, which can only happen if an
    /// endpoint was registered without a policy — the fallback policy exists to make that
    /// impossible, and this is the assertion that proves it.
    /// </exception>
    public static DleCaller RequireDleCaller(this HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.User.GetDleCaller()
            ?? throw new InvalidOperationException(
                "The request has no authenticated tenant. Every control-plane endpoint must carry " +
                "an authorization policy; see DlePolicies.");
    }
}
