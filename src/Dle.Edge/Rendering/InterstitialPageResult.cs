using System.Text;

using Dle.Domain.Attribution;

namespace Dle.Edge.Rendering;

/// <summary>
/// The interstitial page: HTTP 200 HTML whose primary action is a real <c>&lt;a&gt;</c> element
/// (FR-162, FR-163, ADR-009).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an anchor and not a redirect.</b> Inside the Facebook, Instagram and TikTok webviews the
/// operating system only hands a Universal Link or an App Link to the installed application on a
/// genuine tap of an anchor. A scripted <c>window.location</c> is not a user gesture on iOS, so a page
/// that navigates on its own reaches the store — or nothing — and never the app (§A.2.6). Everything
/// else on this page is arranged around that one element: the anchor is written first, it is a real
/// link with a real <c>href</c>, it is reachable by keyboard, and it works with scripting disabled.
/// </para>
/// <para>
/// <b>What is untrusted here.</b> The branding, the link's own title and every URL. The URLs are
/// filtered by <see cref="SafeUrl"/> before they reach an attribute, because HTML encoding does not
/// disarm a <c>javascript:</c> URL; the text is encoded by <see cref="HtmlBuilder"/>; and the response
/// carries a policy with no <c>unsafe-inline</c>, so even a successful injection has no script source to
/// execute from (T-11, S-04).
/// </para>
/// <para>
/// <b>Accessibility (NFR-16).</b> One <c>h1</c>, a <c>main</c> landmark and a <c>contentinfo</c>
/// footer; the anchors are ordinary links with a visible focus ring drawn as an outline so it survives
/// forced-colours mode; the automatic redirect is announced through a <c>status</c> live region and is
/// cancelled by the first keypress, pointer press or focus change, which is what makes a 1.2 second
/// delay compatible with success criterion 2.2.1; nothing traps focus, because there is nothing on the
/// page but links, one button and text.
/// </para>
/// </remarks>
internal sealed class InterstitialPageResult : HtmlPageResult
{
    /// <summary>Longest destination string shown to the user before it is shortened.</summary>
    private const int MaxDisplayLength = 96;

    private readonly string? _deeplinkUrl;
    private readonly string _fallbackUrl;
    private readonly FallbackTarget _fallbackTarget;
    private readonly string _displayTarget;
    private readonly ClientContext _client;
    private readonly DomainRuntimeConfig? _domain;
    private readonly string? _claimCode;
    private readonly bool _isInAppWebView;

    /// <summary>Creates the result.</summary>
    /// <param name="deeplinkUrl">
    /// The deep link the primary anchor carries, already filtered by
    /// <see cref="SafeUrl.Deeplink(string?, ReadOnlySpan{string?})"/>, or <see langword="null"/> when the
    /// link has no in-app target. When it is absent the fallback anchor becomes the primary action.
    /// </param>
    /// <param name="fallbackUrl">The store or web URL, already filtered by <see cref="SafeUrl.Web(string?)"/>.</param>
    /// <param name="fallbackTarget">What the fallback leads to; selects the anchor's label.</param>
    /// <param name="client">The classified client, supplying the language and the channel.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="claimCode">The one-time code issued for this click, or <see langword="null"/> (FR-184).</param>
    internal InterstitialPageResult(
        string? deeplinkUrl,
        string fallbackUrl,
        FallbackTarget fallbackTarget,
        ClientContext client,
        DomainRuntimeConfig? domain,
        string? claimCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(fallbackUrl);
        ArgumentNullException.ThrowIfNull(client);

        _deeplinkUrl = deeplinkUrl;
        _fallbackUrl = fallbackUrl;
        _fallbackTarget = fallbackTarget;
        _displayTarget = Shorten(fallbackUrl);
        _client = client;
        _domain = domain;
        _claimCode = ClaimCode.IsWellFormed(claimCode) ? claimCode : null;

        // ClientChannel numbers in-app browsers 10..19 and documents that grouping as stable. It is
        // the one client property this page changes its behaviour for.
        _isInAppWebView = (int)client.Channel is >= 10 and <= 19;
    }

    /// <inheritdoc />
    protected override int StatusCode => StatusCodes.Status200OK;

    /// <summary>
    /// Serves the ordinary 302 instead of a page when the operator switched the interstitial off.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <remarks>
    /// <c>Dle:Edge:Interstitial:Enabled</c> is a kill switch, and honouring it here rather than in the
    /// resolve pipeline means it cannot be honoured in one code path and forgotten in another. The
    /// redirect is <c>permanent: false</c>, as every link redirect is and must be (ADR-009,
    /// SHARED-KERNEL §17.1).
    /// </remarks>
    public override Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return ResolveOptions(httpContext).Enabled
            ? base.ExecuteAsync(httpContext)
            : Results.Redirect(_fallbackUrl, permanent: false).ExecuteAsync(httpContext);
    }

    /// <inheritdoc />
    protected override string Render(string nonce, InterstitialOptions options)
    {
        PageStrings strings = PageStrings.For(_client, _domain, options);
        PageBranding branding = PageBranding.Resolve(options.Branding, _domain, options);

        // The automatic redirect is suppressed inside an in-app webview on purpose. There the whole
        // point of the page is the tap on the deep link anchor, and navigating away from under the user
        // after 1.2 seconds would remove the only interaction that can open the application (§A.2.6).
        int delayMs = _isInAppWebView ? 0 : options.AutoRedirectMs;
        bool autoRedirect = delayMs > 0;

        HtmlBuilder html = new();

        PageChrome.OpenDocument(
            html,
            nonce,
            strings,
            branding,
            strings.InterstitialDocumentTitle,
            "dle-page-interstitial",
            head: PageChrome.NoIndex);

        html.Raw("<h1 class=\"dle-title\">").Text(strings.InterstitialHeading).Raw("</h1>\n")
            .Raw("<p class=\"dle-lede\">").Text(strings.InterstitialLede).Raw("</p>\n");

        WriteDestination(html, strings);
        WriteActions(html, strings, autoRedirect ? delayMs : null);

        if (autoRedirect)
        {
            WriteAutoRedirectControls(html, strings);
        }

        if (options.ShowClaimCode && _claimCode is not null)
        {
            WriteClaimCode(html, strings, _claimCode);
        }

        PageChrome.CloseDocument(
            html,
            strings,
            branding,
            autoRedirect ? nonce : null,
            autoRedirect ? PageScripts.AutoRedirect : null);

        return html.ToString();
    }

    /// <summary>Writes the block that names where the fallback leads.</summary>
    private void WriteDestination(HtmlBuilder html, PageStrings strings) =>
        html.Raw("<p class=\"dle-target\"><span class=\"dle-target-label\">")
            .Text(strings.DestinationLabel)
            .Raw("</span><span class=\"dle-target-url\">")
            .Text(_displayTarget)
            .Raw("</span></p>\n");

    /// <summary>
    /// Writes the anchors. The deep link comes first in the document, so it is first for a screen reader
    /// and first in the tab order.
    /// </summary>
    /// <param name="html">The writer.</param>
    /// <param name="strings">The resolved copy.</param>
    /// <param name="delayMs">The automatic redirect delay, or <see langword="null"/> when there is none.</param>
    private void WriteActions(HtmlBuilder html, PageStrings strings, int? delayMs)
    {
        html.Raw("<div class=\"dle-actions\">\n");

        if (_deeplinkUrl is not null)
        {
            html.Raw("<a class=\"dle-btn\" id=\"dle-open\" rel=\"nofollow noopener\"")
                .Attribute("href", _deeplinkUrl)
                .Raw(">")
                .Text(strings.OpenInApp)
                .Raw("</a>\n");
        }

        // Primary styling when there is no deep link: the page must never present two secondary
        // actions and no primary one.
        html.Raw(_deeplinkUrl is null
            ? "<a class=\"dle-btn\" id=\"" + PageScripts.ContinueId + "\" rel=\"nofollow noopener noreferrer\""
            : "<a class=\"dle-btn dle-btn-secondary\" id=\"" + PageScripts.ContinueId + "\" rel=\"nofollow noopener noreferrer\"");

        html.Attribute("href", _fallbackUrl);

        if (delayMs is { } delay)
        {
            html.Attribute(PageScripts.DelayAttribute, delay.ToString(CultureInfo.InvariantCulture));
        }

        html.Raw(">")
            .Text(FallbackLabel(strings))
            .Raw("</a>\n")
            .Raw("</div>\n");
    }

    /// <summary>
    /// Writes the live region and the cancel button. Both are inert without scripting: the region is
    /// empty and the button is hidden, so a page with scripting disabled shows neither a promise it
    /// cannot keep nor a control that does nothing.
    /// </summary>
    private static void WriteAutoRedirectControls(HtmlBuilder html, PageStrings strings)
    {
        html.Raw("<p class=\"dle-status\" id=\"" + PageScripts.StatusId + "\" role=\"status\"")
            .Attribute(PageScripts.RunningTextAttribute, strings.CountdownRunning)
            .Attribute(PageScripts.StoppedTextAttribute, strings.CountdownStopped)
            .Raw("></p>\n")
            .Raw("<p><button type=\"button\" class=\"dle-btn dle-btn-quiet\" id=\"" + PageScripts.StopId + "\" hidden>")
            .Text(strings.StopAutoRedirect)
            .Raw("</button></p>\n");
    }

    /// <summary>
    /// Writes the claim code block, the deterministic deferred deep link path on iOS (FR-184, §B.6.3).
    /// </summary>
    /// <remarks>
    /// The code is repeated in an <c>aria-label</c> with the characters separated by spaces, because a
    /// screen reader reading <c>AC4KMP</c> as a word is a code the user cannot transcribe. The visible
    /// text stays unspaced so it can be copied.
    /// </remarks>
    private static void WriteClaimCode(HtmlBuilder html, PageStrings strings, string code)
    {
        html.Raw("<section class=\"dle-claim\" aria-labelledby=\"dle-claim-title\">\n")
            .Raw("<h2 class=\"dle-claim-title\" id=\"dle-claim-title\">")
            .Text(strings.ClaimCodeHeading)
            .Raw("</h2>\n")
            .Raw("<p>")
            .Text(strings.ClaimCodeLede)
            .Raw("</p>\n")
            .Raw("<p><span class=\"dle-code\"")
            .Attribute("aria-label", SpellOut(strings.ClaimCodeAccessiblePrefix, code))
            .Raw(">")
            .Text(code)
            .Raw("</span></p>\n")
            .Raw("<p class=\"dle-claim-note\">")
            .Text(strings.ClaimCodeNote)
            .Raw("</p>\n")
            .Raw("</section>\n");
    }

    /// <summary>Builds "Code: A C 4 K M P" from a prefix and a code.</summary>
    private static string SpellOut(string prefix, string code)
    {
        // Two characters per code character plus the prefix and its separator.
        StringBuilder builder = new(prefix.Length + 2 + (code.Length * 2));

        builder.Append(prefix).Append(": ");

        for (int i = 0; i < code.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(code[i]);
        }

        return builder.ToString();
    }

    /// <summary>Picks the fallback anchor's label.</summary>
    private string FallbackLabel(PageStrings strings) => _fallbackTarget switch
    {
        FallbackTarget.AppStore => strings.ContinueToAppStore,
        FallbackTarget.GooglePlay => strings.ContinueToGooglePlay,
        FallbackTarget.OtherStore => strings.ContinueToStore,
        _ => strings.ContinueToWebsite,
    };

    /// <summary>
    /// Turns a URL into the short, readable form shown on the page: host and path, without the scheme,
    /// the query or the fragment.
    /// </summary>
    /// <param name="url">The URL the anchor carries.</param>
    /// <returns>Text for display. Never used as a URL.</returns>
    /// <remarks>
    /// The query is dropped rather than truncated. It carries the click id and the campaign parameters,
    /// which are noise to the user and are exactly the part of the URL that a phishing target would want
    /// to fill with plausible-looking text.
    /// </remarks>
    private static string Shorten(string url)
    {
        string display = url;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
            (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            string path = parsed.AbsolutePath;

            display = path.Length <= 1
                ? parsed.Authority
                : string.Concat(parsed.Authority, path);
        }

        return display.Length <= MaxDisplayLength
            ? display
            : string.Concat(display.AsSpan(0, MaxDisplayLength - 1), "…");
    }
}
