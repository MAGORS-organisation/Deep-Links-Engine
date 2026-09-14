namespace Dle.Control.Identity;

/// <summary>
/// Names of the authorization policies. Every endpoint carries exactly one.
/// </summary>
/// <remarks>
/// <para>
/// The default is deny. <c>AuthorizationOptions.FallbackPolicy</c> is a policy nothing satisfies, so
/// an endpoint registered without <c>RequireAuthorization</c> refuses the request instead of serving
/// it — a forgotten attribute has to fail closed (SHARED-KERNEL §17.9). Endpoints that are genuinely
/// public say so with <c>AllowAnonymous</c>, which is one word and greppable.
/// </para>
/// <para>
/// A policy is a role floor and a scope, together. The role sets the ceiling of what a credential
/// may ever do; the scope, when the key carries a scope list, lowers it further. Both are checked,
/// never one instead of the other.
/// </para>
/// <para>
/// Each policy also names the authentication schemes it accepts, and this is where §E.2.1 TB2 is
/// enforced. An SDK key ships inside an APK or an IPA and must be assumed extracted, so
/// <see cref="SdkIngest"/> is the only policy that accepts the SDK scheme, and it is the only policy
/// the SDK scheme can satisfy. No confusion of roles or scopes can turn an extracted SDK key into
/// something that writes configuration, because authorization rejects the scheme before it reads a
/// single claim.
/// </para>
/// </remarks>
public static class DlePolicies
{
    /// <summary>Read links, revisions and templates. Viewer, scope <c>links:read</c>.</summary>
    public const string LinksRead = "dle.links.read";

    /// <summary>Create, edit, archive and delete links. Editor, scope <c>links:write</c>.</summary>
    public const string LinksWrite = "dle.links.write";

    /// <summary>Read domains and verification history. Viewer, scope <c>domains:read</c>.</summary>
    public const string DomainsRead = "dle.domains.read";

    /// <summary>Register, verify and remove domains. Admin, scope <c>domains:write</c>.</summary>
    public const string DomainsWrite = "dle.domains.write";

    /// <summary>Read registered applications. Viewer, scope <c>apps:read</c>.</summary>
    public const string AppsRead = "dle.apps.read";

    /// <summary>Register, edit and remove applications. Admin, scope <c>apps:write</c>.</summary>
    public const string AppsWrite = "dle.apps.write";

    /// <summary>Read the caller's own tenant. Viewer, scope <c>tenants:read</c>.</summary>
    public const string TenantsRead = "dle.tenants.read";

    /// <summary>
    /// Provision, edit and deactivate tenants. Owner, scope <c>tenants:write</c>, and the caller must
    /// be recognised as the instance operator.
    /// </summary>
    /// <remarks>
    /// Tenant management is the one control-plane operation that legitimately crosses tenants, so
    /// holding the owner role inside some tenant is not enough. Either
    /// <c>Dle:Control:InstanceTenantId</c> names the tenant whose owners run the instance, or
    /// <c>Dle:Control:AllowTenantSelfService</c> is switched on deliberately. With neither set the
    /// policy denies, which is the correct posture for a deployment that has not decided yet.
    /// </remarks>
    public const string TenantsWrite = "dle.tenants.write";

    /// <summary>List API keys without their secrets. Admin, scope <c>keys:read</c>.</summary>
    public const string KeysRead = "dle.keys.read";

    /// <summary>Issue and revoke API keys. Owner, scope <c>keys:write</c>.</summary>
    public const string KeysWrite = "dle.keys.write";

    /// <summary>Read analytics reports. Viewer, scope <c>analytics:read</c>.</summary>
    public const string AnalyticsRead = "dle.analytics.read";

    /// <summary>
    /// The SDK ingestion policy: the SDK scheme only, and the one scope that scheme ever issues
    /// (§E.2.1, TB2).
    /// </summary>
    public const string SdkIngest = "dle.sdk.ingest";

    /// <summary>The policy nothing satisfies. Used as the fallback for unclassified endpoints.</summary>
    public const string Deny = "dle.deny";
}
