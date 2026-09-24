using Dle.Domain.Links;
using Dle.Domain.Privacy;

namespace Dle.Domain.Ports;

/// <summary>
/// Everything the edge needs to know about a link domain in order to answer a request, in the
/// smallest form that can be cached (FR-145).
/// </summary>
/// <remarks>
/// Both consent modes are carried, not the effective one. The gate computes the effective mode as
/// the stricter of the two, so a domain override can only tighten what the tenant permits, never
/// widen it; pre-computing the result here would throw that invariant away.
/// </remarks>
public sealed record DomainRuntimeConfig
{
    /// <summary>Identifier of the domain.</summary>
    public required Guid Id { get; init; }

    /// <summary>Tenant that owns the domain.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Normalized host, for example <c>link.customer.example</c>.</summary>
    public required string Host { get; init; }

    /// <summary>Consent mode configured on the tenant.</summary>
    public required ConsentMode TenantConsentMode { get; init; }

    /// <summary>Consent mode override configured on the domain, when it has one.</summary>
    public ConsentMode? DomainConsentMode { get; init; }

    /// <summary>Serialized branding for the interstitial page (FR-163).</summary>
    public string? InterstitialBrandJson { get; init; }

    /// <summary>Language used for the interstitial when the client expresses no preference.</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>Open Graph metadata a link falls back to when it defines none (FR-105).</summary>
    public OgMeta? DefaultOg { get; init; }

    /// <summary>Whether the domain currently serves. A disabled domain answers 404 for every
    /// slug, exactly as an unknown host does.</summary>
    public bool IsActive { get; init; }
}
