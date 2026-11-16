using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Control-plane behaviour bound from the <c>Dle:Control</c> configuration section (§C.4).
/// </summary>
/// <remarks>
/// Everything here is a policy decision an operator may reasonably want to change without a
/// rebuild: how a short URL is composed, how large a page or a bulk batch may be, and whether the
/// bundled administration SPA is served at all. Validation runs at startup
/// (<c>ValidateOnStart</c>), so a mistyped value refuses to boot rather than surfacing as a
/// malformed short URL a week later.
/// </remarks>
public sealed class DleControlOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:Control";

    /// <summary>
    /// Scheme used when composing the public short URL of a link from its domain host.
    /// </summary>
    /// <remarks>
    /// Configurable only so that a development deployment behind plain HTTP can still produce URLs
    /// that resolve. Anything other than <c>https</c> in production is a misconfiguration.
    /// </remarks>
    [Required]
    [RegularExpression("^https?$")]
    public string PublicScheme { get; set; } = "https";

    /// <summary>
    /// Path suffix appended to a short URL to reach its QR image on the edge (FR-106).
    /// </summary>
    [Required]
    [StringLength(32, MinimumLength = 1)]
    public string QrPathSuffix { get; set; } = "/qr";

    /// <summary>Default number of items returned by a list endpoint (FR-109).</summary>
    [Range(1, 500)]
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Largest page a caller may ask for.</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 200;

    /// <summary>
    /// Largest number of rows accepted by one NDJSON bulk create request (FR-103, §E.9).
    /// </summary>
    /// <remarks>
    /// The requirement is "at least ten thousand", and the value is a cap rather than a target: the
    /// request is streamed, so the number bounds the work a single caller may queue, not the memory
    /// the server needs to hold.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int BulkMaxRows { get; set; } = 10_000;

    /// <summary>
    /// How many days an association-file change takes to reach every device, quoted back to the
    /// operator after a domain verification (§A.2.1, §A.2.2, TC-125).
    /// </summary>
    [Range(1, 30)]
    public int AssociationPropagationDays { get; set; } = 7;

    /// <summary>How many historical link revisions a single request may read (FR-107).</summary>
    [Range(1, 500)]
    public int MaxLinkVersions { get; set; } = 50;

    /// <summary>
    /// Tenant whose owners administer the instance: the only ones who may provision, edit or
    /// deactivate other tenants.
    /// </summary>
    /// <remarks>
    /// Tenant management crosses tenants, so holding the owner role inside some tenant cannot be
    /// enough on its own. Naming the operator tenant here is the multi-tenant answer;
    /// <see cref="AllowTenantSelfService"/> is the single-organisation one. With neither set, the
    /// tenant found under <c>Dle:Bootstrap:DefaultTenantSlug</c> is the operator while
    /// <c>Dle:Bootstrap:Enabled</c> is on; otherwise the tenant routes deny every caller
    /// (SHARED-KERNEL §17.9).
    /// </remarks>
    public Guid? InstanceTenantId { get; set; }

    /// <summary>
    /// Whether any owner may manage tenants. Off by default; only sensible on a deployment that
    /// serves one organisation.
    /// </summary>
    public bool AllowTenantSelfService { get; set; }

    /// <summary>
    /// Identifier of this process within the deployment, 0 to 1023, used by the link identifier
    /// generator (ADR-007 adjacent: the Snowflake layout of <c>links.id</c>).
    /// </summary>
    /// <remarks>
    /// Two replicas sharing one node identifier can mint the same identifier in the same
    /// millisecond, and the insert that loses is reported as a slug that is already taken. Left
    /// unset, the identifier is derived from the host name (<see cref="DeriveNodeId"/>), which is the
    /// pod name under Kubernetes and the container id under compose, so replicas differ without any
    /// configuration. Set it explicitly, one value per replica, to rule out the remaining
    /// one-in-1024 chance of two host names hashing alike.
    /// </remarks>
    [Range(0, 1023)]
    public int? NodeId { get; set; }

    /// <summary>Maps a host name onto the 0 to 1023 node identifier range, stably.</summary>
    /// <param name="hostName">The host name of this process.</param>
    /// <returns>The same value for the same name, in every process and on every platform.</returns>
    public static int DeriveNodeId(string? hostName)
    {
        // FNV-1a over the UTF-16 code units. string.GetHashCode is randomised per process, which
        // would defeat the point of a stable identifier.
        uint hash = 2166136261;

        foreach (char c in hostName ?? string.Empty)
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)(hash % 1024);
    }

    /// <summary>Whether the bundled administration single-page application is served.</summary>
    /// <remarks>
    /// An API-only deployment turns this off; the static file middleware and the SPA fallback route
    /// are then not registered at all, which is one fewer surface than serving an empty directory.
    /// </remarks>
    public bool ServeAdminSpa { get; set; } = true;

    /// <summary>File served under <c>wwwroot</c> for any SPA route that is neither an API path nor a real file.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string SpaIndexFile { get; set; } = "admin/index.html";

    /// <summary>
    /// Browser origins allowed to call the administration API (<c>/api/*</c>) cross-origin. Empty by
    /// default.
    /// </summary>
    /// <remarks>
    /// The bundled console is served by the control plane itself and needs no entry here. Only a
    /// console hosted elsewhere — a developer's <c>http://localhost:5173</c>, say — does. The SDK
    /// endpoints under <c>/v1</c> are a different matter: they are called from the customer's own web
    /// pages, authenticate with a publishable key in a header, never with a cookie, and therefore allow
    /// any origin regardless of this list.
    /// </remarks>
    public IList<string> AdminCorsOrigins { get; } = [];

    /// <summary>
    /// Whether the OpenAPI document and Scalar API reference endpoints are exposed outside the
    /// <c>Development</c> environment. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ExposeApiDocs { get; set; }

    /// <summary>
    /// When <see langword="true"/> (multi-tenant clusters), newly registered domains stay inactive
    /// (<c>IsActive = false</c>) until DNS TXT ownership (<c>_dle-verify.&lt;host&gt;</c>) is
    /// verified. Defaults to <see langword="false"/> for single-tenant self-hosted deployments.
    /// </summary>
    public bool RequireDomainOwnershipVerification { get; set; }

    /// <summary>
    /// Hours before an unverified inactive domain registration may be reclaimed by another tenant.
    /// </summary>
    [Range(1, 720)]
    public int UnverifiedDomainTtlHours { get; set; } = 24;
}
