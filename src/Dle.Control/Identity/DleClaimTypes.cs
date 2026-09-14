namespace Dle.Control.Identity;

/// <summary>
/// Claim types the control plane puts on an authenticated principal.
/// </summary>
/// <remarks>
/// Short, private names rather than the SOAP-era URIs of <c>ClaimTypes</c>: these claims never leave
/// the process, and a short name keeps the principal small enough that logging it whole is not a
/// mistake waiting to happen.
/// </remarks>
public static class DleClaimTypes
{
    /// <summary>The tenant every query and write in this request is scoped to (FR-241).</summary>
    public const string TenantId = "dle:tenant";

    /// <summary>Instant the tenant was provisioned, for the new-tenant rate limit (§E.9).</summary>
    public const string TenantCreatedAt = "dle:tenant_created_at";

    /// <summary>Identifier of the credential itself: an API key row, an SDK key row, or a user.</summary>
    public const string ActorId = "dle:actor";

    /// <summary>Kind of actor recorded in the audit trail: <c>api_key</c>, <c>sdk_key</c> or <c>user</c>.</summary>
    public const string ActorType = "dle:actor_type";

    /// <summary>The role from <see cref="DleRoles"/>.</summary>
    public const string Role = "dle:role";

    /// <summary>One entry per granted scope from <see cref="DleScopes"/>.</summary>
    public const string Scope = "dle:scope";

    /// <summary>Application an SDK key is bound to.</summary>
    public const string AppId = "dle:app";

    /// <summary>Non-secret prefix of the presented key, safe to log (§E.2.1).</summary>
    public const string KeyPrefix = "dle:key_prefix";
}
