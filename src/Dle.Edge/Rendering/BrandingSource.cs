namespace Dle.Edge.Rendering;

/// <summary>
/// Where the pages the edge renders take their branding from
/// (<c>Dle:Edge:Interstitial:Branding</c>, §C.4, FR-163).
/// </summary>
public enum BrandingSource
{
    /// <summary>No branding: the instance renders a neutral page with no logo and no product name.</summary>
    None = 0,

    /// <summary>The operator's own branding, identical for every tenant on the instance.</summary>
    Instance = 1,

    /// <summary>
    /// The tenant's branding, taken from the link domain's configuration and falling back to the
    /// instance values field by field. This is the default.
    /// </summary>
    Tenant = 2,
}
