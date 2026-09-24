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
    /// <see cref="AllowTenantSelfService"/> is the single-organisation one. With neither set the
    /// tenant routes deny every caller, which is the right posture for a deployment that has not
    /// decided (SHARED-KERNEL §17.9).
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
    /// millisecond, so a multi-replica deployment gives each replica its own — usually from the
    /// ordinal of the stateful set or from the replica index. A single-process deployment can leave
    /// it at zero.
    /// </remarks>
    [Range(0, 1023)]
    public int NodeId { get; set; }

    /// <summary>Whether the bundled administration single-page application is served.</summary>
    /// <remarks>
    /// An API-only deployment turns this off; the static file middleware and the SPA fallback route
    /// are then not registered at all, which is one fewer surface than serving an empty directory.
    /// </remarks>
    public bool ServeAdminSpa { get; set; } = true;

    /// <summary>File served for any SPA route that is neither an API path nor a real file.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string SpaIndexFile { get; set; } = "index.html";
}
