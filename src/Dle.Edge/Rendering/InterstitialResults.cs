namespace Dle.Edge.Rendering;

/// <summary>
/// Every HTML response the resolve pipeline can produce, in one place (§C.3.1, ADR-009).
/// </summary>
/// <remarks>
/// <para>
/// The pipeline calls nothing else to make a page. That is the point: the decisions that must not be
/// made twice — which URL an anchor may carry, which page may see the link, what a 404 is allowed to
/// depend on — are made here, once, and the pipeline chooses only between named outcomes.
/// </para>
/// <para>
/// No page is rendered through a template engine. Razor would compile at first request on a path whose
/// budget is measured in milliseconds and whose first request after a deployment is the one under load
/// (§C.2), and its encoding rules would then be the thing standing between stored Open Graph metadata
/// and a script tag. The pages are written by hand through <see cref="HtmlBuilder"/>, where the encoding
/// is visible at every call site (T-11).
/// </para>
/// </remarks>
internal static class InterstitialResults
{
    /// <summary>Hosts that are Apple's App Store.</summary>
    private static readonly string[] AppStoreHosts = ["apps.apple.com", "itunes.apple.com", "apple.co"];

    /// <summary>Hosts that are Google Play.</summary>
    private static readonly string[] GooglePlayHosts = ["play.google.com", "market.android.com", "play.app.goo.gl"];

    /// <summary>
    /// The interstitial page, whose primary action is a real anchor (FR-162, FR-163).
    /// </summary>
    /// <param name="link">The link being resolved.</param>
    /// <param name="decision">The routing decision that asked for an interstitial.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <param name="client">The classified client.</param>
    /// <returns>A 200 HTML response, or the 404 page when nothing safe can be offered.</returns>
    /// <remarks>
    /// This overload is the one in §C.3.1. It carries no consent decision, so
    /// <see cref="RoutingUrlBuilder"/> treats consent as denied and no click id reaches any URL on the
    /// page. The resolve path uses the overload that takes the gate's decision.
    /// </remarks>
    internal static IResult Page(
        LinkSnapshot link,
        RoutingDecision decision,
        string clickId,
        ClientContext client) =>
        Page(link, decision, clickId, client, consent: null);

    /// <summary>
    /// The interstitial page, whose primary action is a real anchor (FR-162, FR-163).
    /// </summary>
    /// <param name="link">The link being resolved.</param>
    /// <param name="decision">The routing decision that asked for an interstitial.</param>
    /// <param name="clickId">The click identifier for this request.</param>
    /// <param name="client">The classified client.</param>
    /// <param name="consent">
    /// The consent gate's decision. <see langword="null"/> means denied, which is the safe reading of a
    /// caller that has not run the gate (ePrivacy art. 5(3), §E.6.2).
    /// </param>
    /// <param name="domain">The link domain's runtime configuration, for branding and language.</param>
    /// <param name="claimCode">The one-time code issued for this click, or <see langword="null"/> (FR-184).</param>
    /// <returns>A 200 HTML response, or the 404 page when nothing safe can be offered.</returns>
    /// <remarks>
    /// <para>
    /// The fallback anchor points at the store on a mobile platform and at the web target everywhere
    /// else; <see cref="RoutingUrlBuilder.BuildStoreUrl(RoutingDecision, LinkSnapshot, ClientContext, string, ConsentDecision)"/>
    /// already degrades to the web target when the link has no store URL for the platform.
    /// </para>
    /// <para>
    /// When neither the store nor the web URL survives <see cref="SafeUrl"/> the method answers 404
    /// rather than rendering a page with a dead or dangerous anchor. That is the default-deny branch
    /// SHARED-KERNEL §17.9 asks for, and in practice it is unreachable: the control plane validates
    /// <c>TargetUrl</c> on write.
    /// </para>
    /// </remarks>
    internal static IResult Page(
        LinkSnapshot link,
        RoutingDecision decision,
        string clickId,
        ClientContext client,
        ConsentDecision? consent,
        DomainRuntimeConfig? domain = null,
        string? claimCode = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(client);

        string id = clickId ?? string.Empty;
        bool mobile = client.Platform is Platform.Ios or Platform.Android;

        string? fallbackUrl = SafeUrl.Web(mobile
            ? BuildStoreUrl(decision, link, client, id, consent)
            : BuildWebUrl(decision, link, client, id, consent));

        fallbackUrl ??= SafeUrl.Web(link.TargetUrl);

        if (fallbackUrl is null)
        {
            return NotFound(client, domain);
        }

        string? customScheme = client.Platform switch
        {
            Platform.Ios => link.IosCustomScheme,
            Platform.Android => link.AndroidCustomScheme,
            _ => null,
        };

        string? deeplinkUrl = SafeUrl.Deeplink(
            BuildDeeplinkUrl(decision, link, customScheme, id, consent),
            [customScheme]);

        // Two anchors with the same destination is a worse page than one, and it makes the "open in the
        // app" promise a lie when the deep link is really just the web target.
        if (deeplinkUrl is not null && string.Equals(deeplinkUrl, fallbackUrl, StringComparison.Ordinal))
        {
            deeplinkUrl = null;
        }

        return new InterstitialPageResult(
            deeplinkUrl,
            fallbackUrl,
            ClassifyFallback(fallbackUrl, decision),
            client,
            domain,
            claimCode);
    }

    /// <summary>
    /// The interstitial page, built from URLs the caller has already produced (FR-162, FR-163).
    /// </summary>
    /// <param name="link">The link being resolved, supplying the custom scheme the deep link may use.</param>
    /// <param name="decision">The routing decision that asked for an interstitial.</param>
    /// <param name="client">The classified client.</param>
    /// <param name="fallbackUrl">The store or web URL, as built by <see cref="RoutingUrlBuilder"/>.</param>
    /// <param name="deeplinkUrl">The deep link URL, or <see langword="null"/> when the link has none.</param>
    /// <param name="domain">The link domain's runtime configuration, for branding and language.</param>
    /// <param name="claimCode">The one-time code issued for this click, or <see langword="null"/> (FR-184).</param>
    /// <returns>A 200 HTML response, or the 404 page when nothing safe can be offered.</returns>
    /// <remarks>
    /// This is the overload the resolve pipeline uses. It exists so that the URLs are built exactly once,
    /// by the code that already holds the consent decision, and so that the page never becomes a second
    /// place where a redirect target is assembled (SHARED-KERNEL §17.4). Both URLs are still filtered
    /// here: passing them in moves where they are built, not whether they are checked.
    /// </remarks>
    internal static IResult Page(
        LinkSnapshot link,
        RoutingDecision decision,
        ClientContext client,
        string fallbackUrl,
        string? deeplinkUrl,
        DomainRuntimeConfig? domain = null,
        string? claimCode = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(client);

        string? safeFallback = SafeUrl.Web(fallbackUrl) ?? SafeUrl.Web(link.TargetUrl);

        if (safeFallback is null)
        {
            return NotFound(client, domain);
        }

        string? customScheme = client.Platform switch
        {
            Platform.Ios => link.IosCustomScheme,
            Platform.Android => link.AndroidCustomScheme,
            _ => null,
        };

        string? safeDeeplink = SafeUrl.Deeplink(deeplinkUrl, [customScheme]);

        if (safeDeeplink is not null && string.Equals(safeDeeplink, safeFallback, StringComparison.Ordinal))
        {
            safeDeeplink = null;
        }

        return new InterstitialPageResult(
            safeDeeplink,
            safeFallback,
            ClassifyFallback(safeFallback, decision),
            client,
            domain,
            claimCode);
    }

    /// <summary>
    /// The crawler preview: 200 HTML with Open Graph tags and no redirect (FR-105, FR-161, TC-106).
    /// </summary>
    /// <param name="link">The link being previewed.</param>
    /// <returns>A 200 HTML response.</returns>
    internal static IResult OgPreview(LinkSnapshot link) =>
        OgPreview(link, ClientContext.Empty, domain: null, canonicalUrl: null);

    /// <summary>
    /// The crawler preview: 200 HTML with Open Graph tags and no redirect (FR-105, FR-161, TC-106).
    /// </summary>
    /// <param name="link">The link being previewed.</param>
    /// <param name="client">The classified client, supplying the language.</param>
    /// <param name="domain">The link domain's runtime configuration, supplying the default metadata.</param>
    /// <param name="canonicalUrl">
    /// The short URL itself, from <see cref="ShortLinkUrl.Build(string?, string?)"/>. Omitted values
    /// suppress <c>og:url</c> and <c>rel=canonical</c> rather than being guessed.
    /// </param>
    /// <param name="targetUrl">
    /// Where the visible anchor points. Defaults to the link's own target when omitted.
    /// </param>
    /// <returns>A 200 HTML response.</returns>
    internal static IResult OgPreview(
        LinkSnapshot link,
        ClientContext client,
        DomainRuntimeConfig? domain = null,
        string? canonicalUrl = null,
        string? targetUrl = null) =>
        new OgPreviewResult(link, client, domain, canonicalUrl, targetUrl);

    /// <summary>
    /// The 410 page for a link withdrawn by abuse handling (TC-103, §E.3).
    /// </summary>
    /// <param name="client">The classified client, supplying the language. May be <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <returns>A 410 HTML response that redirects nowhere.</returns>
    internal static IResult Gone(ClientContext? client, DomainRuntimeConfig? domain = null) =>
        Gone(client?.Language, domain);

    /// <summary>
    /// The 410 page for a link withdrawn by abuse handling (TC-103, §E.3).
    /// </summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <returns>A 410 HTML response that redirects nowhere.</returns>
    internal static IResult Gone(string? language, DomainRuntimeConfig? domain = null) =>
        new GonePageResult(language, domain);

    /// <summary>
    /// The 404 page. The same page, byte for byte, for every reason a slug does not resolve
    /// (TC-102, TC-166).
    /// </summary>
    /// <param name="client">The classified client, supplying the language. May be <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <returns>A 404 HTML response.</returns>
    /// <remarks>
    /// Neither overload accepts a link, a slug or a tenant, and neither ever will. A missing slug, a
    /// deactivated link and a link owned by another tenant reach this method with identical arguments,
    /// which is what makes the responses identical — including <c>Content-Length</c>, since the only
    /// per-response difference is the fixed-width nonce. A <c>403</c> is never an option
    /// (SHARED-KERNEL §17.7).
    /// </remarks>
    internal static IResult NotFound(ClientContext? client, DomainRuntimeConfig? domain = null) =>
        NotFound(client?.Language, domain);

    /// <summary>
    /// The 404 page. The same page, byte for byte, for every reason a slug does not resolve
    /// (TC-102, TC-166).
    /// </summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <returns>A 404 HTML response.</returns>
    internal static IResult NotFound(string? language, DomainRuntimeConfig? domain = null) =>
        new NotFoundPageResult(language, domain);

    /// <summary>
    /// The 429 page, carrying <c>Retry-After</c> when the limiter reported a wait (§E.9).
    /// </summary>
    /// <param name="client">The classified client, supplying the language. May be <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="retryAfter">How long the client should wait.</param>
    /// <returns>A 429 HTML response.</returns>
    internal static IResult TooManyRequests(
        ClientContext? client,
        DomainRuntimeConfig? domain = null,
        TimeSpan? retryAfter = null) =>
        TooManyRequests(client?.Language, domain, retryAfter);

    /// <summary>
    /// The 429 page, carrying <c>Retry-After</c> when the limiter reported a wait (§E.9).
    /// </summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="retryAfter">How long the client should wait.</param>
    /// <returns>A 429 HTML response.</returns>
    internal static IResult TooManyRequests(
        string? language,
        DomainRuntimeConfig? domain = null,
        TimeSpan? retryAfter = null) =>
        new TooManyRequestsPageResult(language, domain, retryAfter);

    /// <summary>
    /// Works out what the fallback anchor leads to, so that its label can say so.
    /// </summary>
    /// <param name="url">The fallback URL the server built.</param>
    /// <param name="decision">The routing decision, used when the host is not a store we recognise.</param>
    /// <returns>The classification.</returns>
    private static FallbackTarget ClassifyFallback(string url, RoutingDecision decision)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            if (Matches(parsed.Host, AppStoreHosts))
            {
                return FallbackTarget.AppStore;
            }

            if (Matches(parsed.Host, GooglePlayHosts))
            {
                return FallbackTarget.GooglePlay;
            }
        }

        return decision.Kind is DecisionKind.Store ? FallbackTarget.OtherStore : FallbackTarget.Website;
    }

    private static bool Matches(string host, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (host.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildStoreUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        ClientContext client,
        string clickId,
        ConsentDecision? consent) =>
        consent is null
            ? RoutingUrlBuilder.BuildStoreUrl(decision, link, client, clickId)
            : RoutingUrlBuilder.BuildStoreUrl(decision, link, client, clickId, consent);

    private static string BuildWebUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        ClientContext client,
        string clickId,
        ConsentDecision? consent) =>
        consent is null
            ? RoutingUrlBuilder.BuildWebUrl(decision, link, client, clickId)
            : RoutingUrlBuilder.BuildWebUrl(decision, link, client, clickId, consent);

    private static string? BuildDeeplinkUrl(
        RoutingDecision decision,
        LinkSnapshot link,
        string? customScheme,
        string clickId,
        ConsentDecision? consent) =>
        consent is null
            ? RoutingUrlBuilder.BuildDeeplinkUrl(decision, link, customScheme, clickId)
            : RoutingUrlBuilder.BuildDeeplinkUrl(decision, link, customScheme, clickId, consent);
}
