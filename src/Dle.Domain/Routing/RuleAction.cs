namespace Dle.Domain.Routing;

/// <summary>
/// The <c>then</c> half of a routing rule — what happens once the rule has matched.
/// </summary>
/// <remarks>
/// The action describes intent, not the HTTP response. Turning intent into a concrete
/// <see cref="DecisionKind"/> is the job of <see cref="IRoutingEngine"/>, because the same
/// <see cref="RoutingActionKind.AppOrStore"/> action produces an interstitial in an in-app webview
/// and a plain redirect on the desktop.
/// </remarks>
public sealed record RuleAction
{
    /// <summary>What the rule does. Determines which of the URL properties below are mandatory.</summary>
    public required RoutingActionKind Action { get; init; }

    /// <summary>
    /// Absolute web URL, mandatory for the <c>web</c> action. Must use <c>http</c> or <c>https</c>.
    /// When absent for any other action, the link's own target URL is used as the web fallback.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// In-app path handed to the application, for example <c>"/product/123"</c>. It is a path, never a full URL,
    /// and never comes from the incoming request.
    /// </summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>
    /// Absolute store URL, mandatory for the <c>app_or_store</c> and <c>store_only</c> actions.
    /// Existing campaign parameters on this URL are preserved.
    /// </summary>
    public string? StoreUrl { get; init; }

    /// <summary>
    /// Android only. Template for the Play Install Referrer string, written as a URL query string
    /// whose pairs are joined by ampersands, for example <c>dl_cid={click_id}</c> and
    /// <c>utm_source={utm_source}</c>. Supported placeholders are
    /// <c>{click_id}</c>, <c>{utm_source}</c>, <c>{utm_medium}</c>, <c>{utm_campaign}</c>,
    /// <c>{utm_term}</c>, <c>{utm_content}</c> and <c>{link_id}</c>.
    /// </summary>
    public string? ReferrerTemplate { get; init; }

    /// <summary>Per-rule override of the interstitial page.</summary>
    public InterstitialMode Interstitial { get; init; } = InterstitialMode.Auto;
}
