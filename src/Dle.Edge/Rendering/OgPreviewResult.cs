namespace Dle.Edge.Rendering;

/// <summary>
/// The crawler preview: HTTP 200 HTML carrying Open Graph and Twitter Card metadata, and never a
/// redirect (FR-105, FR-161, ADR-009, TC-106).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a redirect.</b> Social crawlers follow 30x responses unreliably and run no
/// JavaScript. A crawler that is redirected either loses the metadata or gives up, and the result is a
/// link preview that renders as a bare URL — the single most visible failure mode a link shortener has
/// (§A.2.6). So the crawler gets a complete static document, and it gets it at the short URL itself.
/// </para>
/// <para>
/// <b>Where the metadata comes from.</b> The link's own values layered over the domain's defaults
/// through <see cref="OgMeta.MergeWith(OgMeta?)"/>, which is what FR-105 means by a fallback: a link
/// that sets only a title still gets the tenant's image and site name. Every value is authored in the
/// control plane by an authenticated operator, and every one of them is still encoded on the way out
/// and, if it is a URL, filtered by <see cref="SafeUrl"/> — a stored value is not a trusted one (T-11).
/// </para>
/// <para>
/// <b>Why it is not cacheable.</b> One URL answers a crawler with this document and a browser with a
/// 302, so a shared cache holding either answer would serve it to the wrong client. The response is
/// therefore <c>no-store</c> rather than <c>public</c> with a <c>Vary</c> the intermediaries would have
/// to be trusted to honour.
/// </para>
/// </remarks>
internal sealed class OgPreviewResult : HtmlPageResult
{
    private readonly OgMeta _og;
    private readonly string? _canonicalUrl;
    private readonly string? _continueUrl;
    private readonly string? _linkTitle;
    private readonly ClientContext _client;
    private readonly DomainRuntimeConfig? _domain;

    /// <summary>Creates the result.</summary>
    /// <param name="link">The link being previewed.</param>
    /// <param name="client">The classified client, supplying the language.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="canonicalUrl">
    /// The short URL itself, used for <c>og:url</c> and <c>rel=canonical</c>. When absent both are
    /// omitted rather than guessed, because a wrong <c>og:url</c> makes a crawler attribute the preview
    /// to somebody else's page.
    /// </param>
    /// <param name="targetUrl">
    /// Where the visible anchor points. Defaults to the link's own target; the resolve pipeline passes
    /// the URL it built so that one implementation decides what a target may be (§17.4).
    /// </param>
    internal OgPreviewResult(
        LinkSnapshot link,
        ClientContext client,
        DomainRuntimeConfig? domain,
        string? canonicalUrl,
        string? targetUrl = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(client);

        _og = link.Og.MergeWith(domain?.DefaultOg);
        _canonicalUrl = SafeUrl.Web(canonicalUrl);
        _continueUrl = SafeUrl.Web(targetUrl) ?? SafeUrl.Web(link.TargetUrl) ?? _canonicalUrl;
        _linkTitle = link.Title;
        _client = client;
        _domain = domain;
    }

    /// <inheritdoc />
    protected override int StatusCode => StatusCodes.Status200OK;

    /// <inheritdoc />
    protected override string Render(string nonce, InterstitialOptions options)
    {
        PageStrings strings = PageStrings.For(_client, _domain, options);
        PageBranding branding = ResolveBranding(options, _domain);

        string title = Coalesce(_og.Title, _linkTitle, branding.ProductName, strings.InterstitialDocumentTitle);
        string? description = Trimmed(_og.Description);
        string? imageUrl = SafeUrl.Web(_og.ImageUrl);

        // The preview is the one page with an image from outside the deployment: the link's
        // own Open Graph picture, on whatever origin the customer put it. Its origin, and no
        // other, joins the policy.
        AllowImage(imageUrl);

        HtmlBuilder html = new();

        PageChrome.OpenDocument(
            html,
            nonce,
            strings,
            branding,
            title,
            "dle-page-preview",
            head: metadata => WriteMetadata(metadata, strings, title, description, imageUrl));

        html.Raw("<h1 class=\"dle-title\">").Text(title).Raw("</h1>\n");

        if (description is not null)
        {
            html.Raw("<p class=\"dle-lede\">").Text(description).Raw("</p>\n");
        }

        if (imageUrl is not null)
        {
            // Decorative: the title and the description carry the same information as real text, so a
            // repeated alternative would be announced twice (WCAG 1.1.1).
            html.Raw("<p><img class=\"dle-preview-image\" alt=\"\" decoding=\"async\" loading=\"lazy\"")
                .Attribute("src", imageUrl)
                .Raw("></p>\n");
        }

        if (_continueUrl is not null)
        {
            html.Raw("<div class=\"dle-actions\"><a class=\"dle-btn\" rel=\"nofollow noopener noreferrer\"")
                .Attribute("href", _continueUrl)
                .Raw(">")
                .Text(strings.PreviewContinue)
                .Raw("</a></div>\n");
        }

        PageChrome.CloseDocument(html, strings, branding);

        return html.ToString();
    }

    /// <summary>
    /// Writes the Open Graph and Twitter Card block, plus the canonical link.
    /// </summary>
    /// <remarks>
    /// Both vocabularies are emitted in full rather than relying on Twitter's documented fallback to
    /// <c>og:*</c>, because several crawlers implement only one of the two and silently render nothing
    /// when the one they read is missing. The canonical link points at the short URL, not at the
    /// destination: the short URL is what was shared, and it is the address that must accumulate the
    /// social signals.
    /// </remarks>
    private void WriteMetadata(
        HtmlBuilder html,
        PageStrings strings,
        string title,
        string? description,
        string? imageUrl)
    {
        if (description is not null)
        {
            html.Meta("name", "description", description);
        }

        if (_canonicalUrl is not null)
        {
            html.Raw("<link rel=\"canonical\"").Attribute("href", _canonicalUrl).Raw(">\n");
        }

        html.Meta("property", "og:type", Coalesce(_og.Type, OgMeta.DefaultType))
            .Meta("property", "og:title", title)
            .Meta("property", "og:description", description)
            .Meta("property", "og:url", _canonicalUrl)
            .Meta("property", "og:image", imageUrl)
            .Meta("property", "og:site_name", _og.SiteName)
            .Meta("property", "og:locale", Locale(strings.LanguageTag))
            .Meta("name", "twitter:card", Coalesce(_og.TwitterCard, OgMeta.DefaultTwitterCard))
            .Meta("name", "twitter:title", title)
            .Meta("name", "twitter:description", description)
            .Meta("name", "twitter:image", imageUrl);
    }

    /// <summary>Maps a language tag to the underscore form Open Graph expects.</summary>
    private static string Locale(string languageTag) => languageTag switch
    {
        "sk" => "sk_SK",
        _ => "en_US",
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Coalesce(string? first, string second) => Trimmed(first) ?? second;

    private static string Coalesce(string? first, string? second, string? third, string fourth) =>
        Trimmed(first) ?? Trimmed(second) ?? Trimmed(third) ?? fourth;
}
