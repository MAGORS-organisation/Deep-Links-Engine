namespace Dle.Domain.Routing;

/// <summary>
/// Per-rule override for the interstitial page (FR-162, FR-163).
/// </summary>
/// <remarks>
/// The interstitial exists because an in-app webview only hands a Universal Link or an App Link
/// to the operating system on a genuine tap of an <c>&lt;a&gt;</c> element; a scripted navigation
/// is not a user gesture (§A.2.6). <see cref="Never"/> therefore never suppresses the interstitial
/// for an in-app webview — it only suppresses it where a plain redirect actually works.
/// </remarks>
public enum InterstitialMode
{
    /// <summary>Decide from the client: interstitial for in-app webviews and mobile, redirect for desktop.</summary>
    Auto = 0,

    /// <summary>Always render the interstitial page, even for an ordinary mobile browser.</summary>
    Always = 1,

    /// <summary>Never render the interstitial for clients where a redirect works; in-app webviews still get it.</summary>
    Never = 2,
}
