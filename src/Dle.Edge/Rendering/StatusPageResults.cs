namespace Dle.Edge.Rendering;

/// <summary>
/// Shared rendering for the three pages that answer a request the edge will not resolve: 404, 410 and
/// 429.
/// </summary>
/// <remarks>
/// <para>
/// They share a base class rather than a helper method because what matters about them is what they
/// have in common. Each is built from the same chrome, the same branding and the same copy, and each is
/// constructed from the link domain and the client's language and from nothing else. A status page that
/// could see the link would be a page that could leak whether the link exists.
/// </para>
/// <para>
/// None of them carries a script, and none of them redirects. A 410 in particular must not: the point
/// of the quarantine is that the destination is no longer served (TC-103).
/// </para>
/// </remarks>
internal abstract class StatusPageResult : HtmlPageResult
{
    private readonly string? _language;
    private readonly DomainRuntimeConfig? _domain;

    /// <summary>Creates the result.</summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    private protected StatusPageResult(string? language, DomainRuntimeConfig? domain)
    {
        _language = language;
        _domain = domain;
    }

    /// <summary>Class placed on the <c>body</c> element, so the page can be styled and identified.</summary>
    protected abstract string BodyClass { get; }

    /// <summary>Title of the document.</summary>
    /// <param name="strings">The resolved copy.</param>
    /// <returns>The title.</returns>
    protected abstract string DocumentTitle(PageStrings strings);

    /// <summary>Heading of the page.</summary>
    /// <param name="strings">The resolved copy.</param>
    /// <returns>The heading.</returns>
    protected abstract string Heading(PageStrings strings);

    /// <summary>Writes the body of the page, positioned after the heading and inside the card.</summary>
    /// <param name="html">The writer.</param>
    /// <param name="strings">The resolved copy.</param>
    /// <param name="options">The resolved page configuration.</param>
    protected abstract void WriteBody(HtmlBuilder html, PageStrings strings, InterstitialOptions options);

    /// <inheritdoc />
    protected sealed override string Render(string nonce, InterstitialOptions options)
    {
        PageStrings strings = PageStrings.For(_language, _domain, options);
        PageBranding branding = PageBranding.Resolve(options.Branding, _domain, options);

        HtmlBuilder html = new(4 * 1024);

        PageChrome.OpenDocument(
            html,
            nonce,
            strings,
            branding,
            DocumentTitle(strings),
            BodyClass,
            head: PageChrome.NoIndex);

        html.Raw("<h1 class=\"dle-title\">").Text(Heading(strings)).Raw("</h1>\n")
            .Raw("<div class=\"dle-prose\">\n");

        WriteBody(html, strings, options);

        html.Raw("</div>\n");

        PageChrome.CloseDocument(html, strings, branding);

        return html.ToString();
    }

    /// <summary>Writes one paragraph of encoded copy.</summary>
    /// <param name="html">The writer.</param>
    /// <param name="text">The paragraph.</param>
    private protected static void Paragraph(HtmlBuilder html, string text) =>
        html.Raw("<p>").Text(text).Raw("</p>\n");
}

/// <summary>
/// The 404 page. One page answers every reason a slug does not resolve.
/// </summary>
/// <remarks>
/// <para>
/// This is the anti-enumeration surface, and the way it is built is the guarantee. The constructor
/// takes the client's language and the link domain, and there is deliberately no overload that takes a
/// link, a slug or a tenant: a missing slug, a deactivated link, an expired link with no expiry URL and
/// a link belonging to a different tenant all reach this type with byte-identical arguments, so they
/// produce byte-identical markup (TC-102, TC-166, SHARED-KERNEL §17.7).
/// </para>
/// <para>
/// The only per-response variation anywhere in the response is the nonce, which is a fixed 24 characters
/// of base64 for every response, so <c>Content-Length</c> is identical too and no length oracle exists
/// either.
/// </para>
/// </remarks>
internal sealed class NotFoundPageResult : StatusPageResult
{
    /// <summary>Creates the result.</summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    internal NotFoundPageResult(string? language, DomainRuntimeConfig? domain)
        : base(language, domain)
    {
    }

    /// <inheritdoc />
    protected override int StatusCode => StatusCodes.Status404NotFound;

    /// <inheritdoc />
    protected override string BodyClass => "dle-page-notfound";

    /// <inheritdoc />
    protected override string DocumentTitle(PageStrings strings) => strings.NotFoundDocumentTitle;

    /// <inheritdoc />
    protected override string Heading(PageStrings strings) => strings.NotFoundHeading;

    /// <inheritdoc />
    protected override void WriteBody(HtmlBuilder html, PageStrings strings, InterstitialOptions options)
    {
        ArgumentNullException.ThrowIfNull(strings);

        Paragraph(html, strings.NotFoundBody);
        Paragraph(html, strings.NotFoundHint);
    }
}

/// <summary>
/// The 410 page served for a link withdrawn by abuse handling (TC-103, §E.3).
/// </summary>
/// <remarks>
/// It explains what happened, says that the record was kept rather than deleted, and offers the appeal
/// channel the operator configured. That is not decoration: Article 16 of the Digital Services Act
/// requires a notice and action mechanism, and a mechanism nobody can find is not one. When the operator
/// configured neither an appeal form nor an appeal mailbox the page says so plainly instead of
/// pretending there is no route.
/// </remarks>
internal sealed class GonePageResult : StatusPageResult
{
    /// <summary>Creates the result.</summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    internal GonePageResult(string? language, DomainRuntimeConfig? domain)
        : base(language, domain)
    {
    }

    /// <inheritdoc />
    protected override int StatusCode => StatusCodes.Status410Gone;

    /// <inheritdoc />
    protected override string BodyClass => "dle-page-gone";

    /// <inheritdoc />
    protected override string DocumentTitle(PageStrings strings) => strings.GoneDocumentTitle;

    /// <inheritdoc />
    protected override string Heading(PageStrings strings) => strings.GoneHeading;

    /// <inheritdoc />
    protected override void WriteBody(HtmlBuilder html, PageStrings strings, InterstitialOptions options)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(options);

        Paragraph(html, strings.GoneBody);
        Paragraph(html, strings.GoneRetention);

        html.Raw("<h2>").Text(strings.GoneAppealHeading).Raw("</h2>\n");

        Paragraph(html, strings.GoneAppealBody);

        string? appealUrl = SafeUrl.Https(options.AppealUrl);

        if (appealUrl is not null)
        {
            WriteAppealAnchor(html, appealUrl, strings.GoneAppealFormLabel);
        }
        else if (SafeUrl.MailTo(options.AppealEmail) is { } mailTo)
        {
            WriteAppealAnchor(html, mailTo, strings.GoneAppealEmailLabel);
        }
        else
        {
            // Neither channel is configured. The page says where to go instead of rendering a control
            // that leads nowhere (SHARED-KERNEL §17.9: the branch has an explicit safe outcome).
            Paragraph(html, strings.GoneAppealUnconfigured);
        }

        html.Raw("<p class=\"dle-legal\">").Text(strings.GoneLegalNote).Raw("</p>\n");
    }

    private static void WriteAppealAnchor(HtmlBuilder html, string url, string label) =>
        html.Raw("<p><a class=\"dle-btn dle-btn-secondary\" rel=\"nofollow noopener noreferrer\"")
            .Attribute("href", url)
            .Raw(">")
            .Text(label)
            .Raw("</a></p>\n");
}

/// <summary>
/// The 429 page served when a limiter refused the request (§E.9).
/// </summary>
/// <remarks>
/// The page exists because a rate limited human is a normal event — a shared mobile network, an office
/// egress address, a link posted to a large channel — and an empty 429 is indistinguishable from the
/// service being broken. It reports the same wait as the <c>Retry-After</c> header so that the two
/// cannot disagree.
/// </remarks>
internal sealed class TooManyRequestsPageResult : StatusPageResult
{
    private readonly TimeSpan? _retryAfter;

    /// <summary>Creates the result.</summary>
    /// <param name="language">Primary language subtag from the request, or <see langword="null"/>.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="retryAfter">How long the client should wait, when the limiter reports it.</param>
    internal TooManyRequestsPageResult(string? language, DomainRuntimeConfig? domain, TimeSpan? retryAfter)
        : base(language, domain) =>
        _retryAfter = retryAfter is { } value && value > TimeSpan.Zero ? value : null;

    /// <inheritdoc />
    protected override int StatusCode => StatusCodes.Status429TooManyRequests;

    /// <inheritdoc />
    protected override TimeSpan? RetryAfter => _retryAfter;

    /// <inheritdoc />
    protected override string BodyClass => "dle-page-throttled";

    /// <inheritdoc />
    protected override string DocumentTitle(PageStrings strings) => strings.TooManyDocumentTitle;

    /// <inheritdoc />
    protected override string Heading(PageStrings strings) => strings.TooManyHeading;

    /// <inheritdoc />
    protected override void WriteBody(HtmlBuilder html, PageStrings strings, InterstitialOptions options)
    {
        ArgumentNullException.ThrowIfNull(strings);

        Paragraph(html, strings.TooManyBody);

        if (_retryAfter is { } retryAfter)
        {
            long seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));

            Paragraph(
                html,
                string.Format(
                    CultureInfo.InvariantCulture,
                    strings.TooManyRetryAfterFormat,
                    seconds.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            Paragraph(html, strings.TooManyRetryGeneric);
        }
    }
}
