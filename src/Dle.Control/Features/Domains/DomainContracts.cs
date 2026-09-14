namespace Dle.Control.Features.Domains;

/// <summary>
/// Body of <c>PATCH /api/v1/domains/{id}</c>. Every member is optional; only the supplied ones
/// change.
/// </summary>
/// <remarks>
/// The host is deliberately absent. Renaming a domain would silently break every short URL already
/// in circulation on the old host, so a move is register-and-retire, not an edit — which is also the
/// only sequence in which the association files on both hosts stay correct throughout (§A.2.1).
/// </remarks>
public sealed record UpdateDomainRequest
{
    /// <summary>Make this the default domain for new links in the tenant.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Switch serving on or off for this domain.</summary>
    public bool? IsActive { get; init; }

    /// <summary>
    /// Consent mode override: <c>off</c>, <c>aggregate_only</c> or <c>full</c>. It may only tighten
    /// the tenant setting; an empty string clears the override (§E.6.2).
    /// </summary>
    public string? ConsentModeOverride { get; init; }

    /// <summary>Replacement default Open Graph metadata for links on this domain (FR-105).</summary>
    public OgMeta? DefaultOg { get; init; }
}
