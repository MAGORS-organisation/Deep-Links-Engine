namespace Dle.Domain.Entities;

/// <summary>
/// A report submitted through the public abuse form (FR-245). Maps to <c>abuse_reports</c>.
/// </summary>
/// <remarks>
/// The form is not only hygiene. A link shortener carrying user supplied content is very likely a
/// hosting service under the Digital Services Act, whose article 16 requires a notice and action
/// mechanism with a traceable outcome — which is what the status and the resolution note provide
/// (§E.3).
/// </remarks>
public class AbuseReport
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>The reported link.</summary>
    public long LinkId { get; set; }

    /// <summary>Tenant that owns the reported link.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Category, stored as text: <c>phishing</c>, <c>malware</c>, <c>spam</c>,
    /// <c>illegal</c>, <c>copyright</c> or <c>other</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Free text description supplied by the reporter.</summary>
    public string? Details { get; set; }

    /// <summary>Hash of the reporter email address. Hashed rather than stored, so that the
    /// operator can recognise a repeat reporter and deduplicate reports without keeping a list of
    /// people who reported someone.</summary>
    public byte[]? ReporterEmailHash { get; set; }

    /// <summary>Handling state, stored as text: <c>new</c>, <c>triaged</c>, <c>confirmed</c>,
    /// <c>rejected</c> or <c>resolved</c>.</summary>
    public string Status { get; set; } = "new";

    /// <summary>Submission instant, in UTC. The reaction target is counted from here.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Instant the report was closed, in UTC.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>What the operator decided and why.</summary>
    public string? ResolutionNote { get; set; }
}
