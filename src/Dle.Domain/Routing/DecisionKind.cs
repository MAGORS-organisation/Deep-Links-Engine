namespace Dle.Domain.Routing;

/// <summary>
/// The response class that the edge turns into an HTTP response (ADR-009).
/// </summary>
/// <remarks>
/// A link is never answered with <c>301</c>: a permanent redirect is cached by browsers and CDNs
/// forever and would break A/B splits, time windows and target changes (ADR-009, FR-164).
/// </remarks>
public enum DecisionKind
{
    /// <summary><c>302</c> to the web fallback.</summary>
    Web = 0,

    /// <summary><c>302</c> to the App Store or Play Store URL, carrying campaign or referrer parameters.</summary>
    Store = 1,

    /// <summary>Open the installed application directly, without offering the store.</summary>
    AppDirect = 2,

    /// <summary><c>200</c> interstitial HTML with a real <c>&lt;a&gt;</c> button (FR-162).</summary>
    Interstitial = 3,

    /// <summary>The rule set refused to serve this client.</summary>
    Blocked = 4,

    /// <summary>No rule matched, or the link is not servable. Answered with <c>404</c>.</summary>
    NotFound = 5,

    /// <summary>The link existed but was withdrawn (quarantined). Answered with <c>410</c> (TC-103).</summary>
    Gone = 6,

    /// <summary><c>200</c> HTML preview with Open Graph tags, for crawlers and <c>?_dl=preview</c> (FR-161, FR-166).</summary>
    Preview = 7,
}
