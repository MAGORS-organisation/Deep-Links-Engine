namespace Dle.Domain.Entities;

/// <summary>
/// A host that serves links, together with its verification state (FR-145). Maps to
/// <c>domains</c>.
/// </summary>
/// <remarks>
/// The verification columns are separate per platform on purpose: a domain can serve Android app
/// links correctly while its Apple association file is broken, and collapsing the two into one
/// status would hide exactly the half that is failing.
/// </remarks>
public class LinkDomain
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Case insensitive unique host, for example <c>link.customer.example</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Whether this is the default host for new links of the tenant.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Certificate state, stored as text: <c>pending</c>, <c>ok</c> or <c>failed</c>.</summary>
    public string TlsStatus { get; set; } = "pending";

    /// <summary>State of the Apple app site association file, stored as text.</summary>
    public string AasaStatus { get; set; } = "pending";

    /// <summary>State of the Digital Asset Links file, stored as text.</summary>
    public string AssetlinksStatus { get; set; } = "pending";

    /// <summary>Last time the domain was verified, in UTC.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>History of verification runs, stored as a JSON array (FR-143).</summary>
    public string VerificationLog { get; set; } = "[]";

    /// <summary>Optional consent mode override, stored as text. It can only tighten the mode of
    /// the tenant, never widen it.</summary>
    public string? ConsentModeOverride { get; set; }

    /// <summary>Interstitial branding, stored as a JSON document (FR-163).</summary>
    public string Branding { get; set; } = "{}";

    /// <summary>Open Graph defaults for links that define none, stored as a JSON document (FR-105).</summary>
    public string DefaultOg { get; set; } = "{}";

    /// <summary>Whether the domain serves. A disabled domain answers 404 like an unknown host.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
