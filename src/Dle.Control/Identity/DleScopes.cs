namespace Dle.Control.Identity;

/// <summary>
/// Scopes that narrow what a credential may do inside its role (FR-242).
/// </summary>
/// <remarks>
/// <para>
/// Role and scope are an AND, never an OR. The role sets the ceiling; the scope list, when a key
/// carries one, lowers it further. A key with the <c>admin</c> role and the single scope
/// <c>links:read</c> can read links and nothing else — which is what makes a key issued to a
/// reporting job safe to leave in a scheduler.
/// </para>
/// <para>
/// A key with an empty scope list is governed by its role alone. That is the common case and the
/// documented default; it is not "all scopes granted" in disguise, because the role check still
/// runs.
/// </para>
/// </remarks>
public static class DleScopes
{
    /// <summary>Read links, their revisions and their templates.</summary>
    public const string LinksRead = "links:read";

    /// <summary>Create, edit, archive and delete links.</summary>
    public const string LinksWrite = "links:write";

    /// <summary>Read domains and their verification history.</summary>
    public const string DomainsRead = "domains:read";

    /// <summary>Register, edit, verify and remove domains.</summary>
    public const string DomainsWrite = "domains:write";

    /// <summary>Read registered applications.</summary>
    public const string AppsRead = "apps:read";

    /// <summary>Register, edit and remove applications and their SDK keys.</summary>
    public const string AppsWrite = "apps:write";

    /// <summary>Read tenant configuration.</summary>
    public const string TenantsRead = "tenants:read";

    /// <summary>Provision, edit and deactivate tenants.</summary>
    public const string TenantsWrite = "tenants:write";

    /// <summary>List API keys, without ever seeing their secrets.</summary>
    public const string KeysRead = "keys:read";

    /// <summary>Issue and revoke API keys.</summary>
    public const string KeysWrite = "keys:write";

    /// <summary>Read analytics reports.</summary>
    public const string AnalyticsRead = "analytics:read";

    /// <summary>
    /// The only scope an SDK key ever carries: report events and resolve a deferred link.
    /// </summary>
    /// <remarks>
    /// It appears in no other policy. A credential holding it and nothing else can reach
    /// <c>/v1/resolve</c> and <c>/v1/events</c>, and that is the whole of its reach (§E.2.1, TB2).
    /// </remarks>
    public const string SdkIngest = "sdk:ingest";

    /// <summary>Every scope a control-plane key may be issued with.</summary>
    /// <remarks>
    /// <see cref="SdkIngest"/> is deliberately absent: it belongs to a different credential type and
    /// must not be grantable to a control-plane key, which would otherwise be a way to smuggle
    /// control-plane reach into the SDK policy.
    /// </remarks>
    public static readonly string[] All =
    [
        LinksRead,
        LinksWrite,
        DomainsRead,
        DomainsWrite,
        AppsRead,
        AppsWrite,
        TenantsRead,
        TenantsWrite,
        KeysRead,
        KeysWrite,
        AnalyticsRead,
    ];

    /// <summary>Whether a scope is one a control-plane key may be issued with.</summary>
    /// <param name="scope">The candidate value.</param>
    /// <returns><see langword="true"/> when the scope is grantable.</returns>
    public static bool IsGrantable(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        foreach (string candidate in All)
        {
            if (string.Equals(candidate, scope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
