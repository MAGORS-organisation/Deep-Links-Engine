using System.ComponentModel.DataAnnotations;

namespace Dle.Edge.Rendering;

/// <summary>
/// Configuration of every page the edge renders: the interstitial, the crawler preview and the
/// three status pages. Bound from <c>Dle:Edge:Interstitial</c> (SHARED-KERNEL §16, §C.4).
/// </summary>
/// <remarks>
/// The section is named after the interstitial because that is the page the operator thinks about, but
/// the branding, the language default and the appeal contact belong to all of them: a 410 page that
/// does not look like the link the user just clicked is worse than useless.
/// </remarks>
public sealed class InterstitialOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:Interstitial";

    /// <summary>Default of <see cref="AutoRedirectMs"/>, in milliseconds.</summary>
    public const int DefaultAutoRedirectMs = 1200;

    /// <summary>
    /// Whether the resolve pipeline may answer with an interstitial at all. When false, a routing rule
    /// asking for one degrades to the ordinary 302.
    /// </summary>
    /// <remarks>
    /// The renderer honours this flag by refusing to build a page, so a misconfigured deployment fails
    /// closed rather than serving a page the operator has switched off.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Delay before the page navigates to the fallback on its own, in milliseconds. Zero disables the
    /// automatic navigation entirely.
    /// </summary>
    /// <remarks>
    /// This is a progressive enhancement and never the only path: the page always carries a real anchor
    /// the user can tap, because inside the Facebook, Instagram and TikTok webviews a Universal Link
    /// only fires on a genuine tap - <c>window.location</c> from a script is not a user gesture on iOS
    /// (§A.2.6, FR-162). The countdown can also be stopped from the page, which is what keeps a timed
    /// navigation compatible with WCAG 2.2 success criterion 2.2.1 (NFR-16).
    /// </remarks>
    [Range(0, 60_000)]
    public int AutoRedirectMs { get; set; } = DefaultAutoRedirectMs;

    /// <summary>Where branding comes from. Defaults to <see cref="BrandingSource.Tenant"/>.</summary>
    public BrandingSource Branding { get; set; } = BrandingSource.Tenant;

    /// <summary>
    /// Product name shown when the tenant supplies none, or when <see cref="Branding"/> is
    /// <see cref="BrandingSource.Instance"/>.
    /// </summary>
    [StringLength(64)]
    public string? ProductName { get; set; }

    /// <summary>Absolute <c>https</c> URL of the instance logo. Values on any other scheme are ignored.</summary>
    [StringLength(512)]
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Where a tenant whose link was quarantined after an abuse report appeals the decision
    /// (§E.3, TC-103). Shown on the 410 page.
    /// </summary>
    [StringLength(512)]
    public string? AppealUrl { get; set; }

    /// <summary>Mailbox that accepts appeals, shown on the 410 page when <see cref="AppealUrl"/> is unset.</summary>
    [StringLength(254)]
    public string? AppealEmail { get; set; }

    /// <summary>Absolute <c>https</c> URL of the operator's support or contact page, shown in the footer.</summary>
    [StringLength(512)]
    public string? SupportUrl { get; set; }

    /// <summary>Absolute <c>https</c> URL of the privacy notice, shown in the footer.</summary>
    [StringLength(512)]
    public string? PrivacyUrl { get; set; }

    /// <summary>
    /// Whether the interstitial may show a claim code when the resolve pipeline issued one
    /// (FR-184, §B.6.3). The code is only rendered when one is actually present.
    /// </summary>
    public bool ShowClaimCode { get; set; } = true;

    /// <summary>
    /// Language used when neither the client nor the link domain expresses a usable preference.
    /// Only <c>en</c> and <c>sk</c> are shipped (NFR-15).
    /// </summary>
    [StringLength(8)]
    public string DefaultLanguage { get; set; } = "en";
}
