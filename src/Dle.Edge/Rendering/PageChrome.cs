using System.Globalization;

namespace Dle.Edge.Rendering;

/// <summary>
/// The parts of the document every rendered page shares: the head, the branding header, the footer and
/// the closing tags.
/// </summary>
/// <remarks>
/// One implementation of the chrome means one place where the <c>lang</c> attribute, the landmarks, the
/// stylesheet nonce and the icon reference can be wrong, and one place where they can be fixed. The
/// pages differ only in their content.
/// </remarks>
internal static class PageChrome
{
    /// <summary>
    /// Head content for a page that must stay out of search indexes: the interstitial and the three
    /// status pages.
    /// </summary>
    /// <remarks>
    /// <c>robots.txt</c> deliberately allows the whole origin, because a <c>Disallow</c> would also stop
    /// the social crawlers that build link previews (FR-161). Suppression is therefore per page, and
    /// only the crawler preview omits it.
    /// </remarks>
    internal static readonly Action<HtmlBuilder> NoIndex =
        static html => html.Raw("<meta name=\"robots\" content=\"noindex, nofollow\">\n");

    /// <summary>
    /// Writes everything from the doctype to the opening of the card, leaving the caller positioned
    /// inside <c>&lt;main&gt;</c>.
    /// </summary>
    /// <param name="html">The writer.</param>
    /// <param name="nonce">The response nonce.</param>
    /// <param name="strings">The resolved copy, which also supplies the language tag.</param>
    /// <param name="branding">Validated branding.</param>
    /// <param name="documentTitle">Title of the document; untrusted values are encoded.</param>
    /// <param name="bodyClass">Extra class on the <c>body</c> element; a literal.</param>
    /// <param name="bodyAttributes">Optional extra attributes on the <c>body</c> element; a literal.</param>
    /// <param name="head">
    /// Writes additional head content immediately after the title, before the stylesheet. Used for the
    /// robots directive and for the Open Graph block, which has to precede anything a crawler might
    /// stop reading at.
    /// </param>
    internal static void OpenDocument(
        HtmlBuilder html,
        string nonce,
        PageStrings strings,
        PageBranding branding,
        string documentTitle,
        string bodyClass,
        string? bodyAttributes = null,
        Action<HtmlBuilder>? head = null)
    {
        html.Raw("<!doctype html>\n<html")
            .Attribute("lang", strings.LanguageTag)
            .Raw(" dir=\"ltr\">\n<head>\n")
            .Raw("<meta charset=\"utf-8\">\n")
            .Raw("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">\n")
            .Raw("<meta name=\"color-scheme\" content=\"light dark\">\n")
            .Raw("<title>")
            .Text(documentTitle)
            .Raw("</title>\n");

        head?.Invoke(html);

        html.Raw("<link rel=\"icon\" type=\"image/svg+xml\" href=\"" + PageStyles.FaviconPath + "\">\n")
            .Raw("<style")
            .Attribute("nonce", nonce)
            .Raw(">")
            .Raw(PageStyles.CriticalCss);

        WriteBrandColors(html, branding);

        html.Raw("</style>\n")
            .Raw("<link rel=\"stylesheet\" href=\"" + PageStyles.StylesheetPath + "\">\n")
            .Raw("</head>\n<body class=\"")
            .Raw(bodyClass)
            .Raw("\"");

        if (bodyAttributes is not null)
        {
            html.Raw(bodyAttributes);
        }

        html.Raw(">\n<div class=\"dle-shell\">\n<main id=\"dle-main\" class=\"dle-card\">\n");

        WriteBrandHeader(html, branding);
    }

    /// <summary>
    /// Closes the card and writes the footer and the closing tags.
    /// </summary>
    /// <param name="html">The writer.</param>
    /// <param name="strings">The resolved copy.</param>
    /// <param name="branding">Validated branding.</param>
    /// <param name="scriptNonce">Nonce for a trailing script, or <see langword="null"/> for no script.</param>
    /// <param name="script">The script body; a literal.</param>
    internal static void CloseDocument(
        HtmlBuilder html,
        PageStrings strings,
        PageBranding branding,
        string? scriptNonce = null,
        string? script = null)
    {
        html.Raw("</main>\n");

        bool hasSupport = branding.SupportUrl is not null;
        bool hasPrivacy = branding.PrivacyUrl is not null;

        if (hasSupport || hasPrivacy)
        {
            html.Raw("<footer class=\"dle-foot\">\n<ul>\n");

            if (hasSupport)
            {
                html.Raw("<li><a rel=\"nofollow noopener noreferrer\"")
                    .Attribute("href", branding.SupportUrl)
                    .Raw(">")
                    .Text(strings.SupportLabel)
                    .Raw("</a></li>\n");
            }

            if (hasPrivacy)
            {
                html.Raw("<li><a rel=\"nofollow noopener noreferrer\"")
                    .Attribute("href", branding.PrivacyUrl)
                    .Raw(">")
                    .Text(strings.PrivacyLabel)
                    .Raw("</a></li>\n");
            }

            html.Raw("</ul>\n</footer>\n");
        }

        html.Raw("</div>\n");

        if (scriptNonce is not null && script is not null)
        {
            html.Raw("<script")
                .Attribute("nonce", scriptNonce)
                .Raw(">")
                .Raw(script)
                .Raw("</script>\n");
        }

        html.Raw("</body>\n</html>\n");
    }

    /// <summary>
    /// Writes the logo and product name, or the built-in mark when the tenant configured neither.
    /// </summary>
    /// <param name="html">The writer.</param>
    /// <param name="branding">Validated branding.</param>
    private static void WriteBrandHeader(HtmlBuilder html, PageBranding branding)
    {
        if (branding.IsEmpty)
        {
            return;
        }

        html.Raw("<header class=\"dle-brand\">\n");

        if (branding.LogoUrl is not null)
        {
            // The alt text is empty on purpose: the product name follows as real text, and a logo that
            // repeats it would be announced twice (WCAG 1.1.1, decorative image).
            html.Raw("<img class=\"dle-logo\" alt=\"\" decoding=\"async\" loading=\"eager\"")
                .Attribute("src", branding.LogoUrl)
                .Raw(">\n");
        }

        if (branding.ProductName is not null)
        {
            html.Raw("<span>").Text(branding.ProductName).Raw("</span>\n");
        }

        html.Raw("</header>\n");
    }

    /// <summary>
    /// Emits the branding colour overrides as custom property declarations.
    /// </summary>
    /// <param name="html">The writer.</param>
    /// <param name="branding">Validated branding.</param>
    /// <remarks>
    /// <para>
    /// Only the button's fill is overridden, and its ink is computed from the fill rather than taken
    /// from configuration. A tenant that picks a pale accent gets dark text on it automatically, so the
    /// contrast floor of NFR-16 holds for every branding value rather than for the ones an operator
    /// happened to test. The link colour is never overridden for the same reason: it sits on the page
    /// background, where a tenant colour could not be corrected without also changing the background.
    /// </para>
    /// <para>
    /// The values reaching this method have already been through <see cref="SafeUrl.HexColor(string?)"/>,
    /// so they cannot close the declaration and start a rule of their own.
    /// </para>
    /// </remarks>
    private static void WriteBrandColors(HtmlBuilder html, PageBranding branding)
    {
        if (branding.AccentColor is { } light)
        {
            html.Raw(":root{--dle-accent:")
                .Raw(light)
                .Raw(";--dle-on-accent:")
                .Raw(ContrastingInk(light))
                .Raw("}");
        }

        if (branding.AccentColorDark is { } dark)
        {
            html.Raw("@media (prefers-color-scheme:dark){:root{--dle-accent:")
                .Raw(dark)
                .Raw(";--dle-on-accent:")
                .Raw(ContrastingInk(dark))
                .Raw("}}");
        }
    }

    /// <summary>
    /// Picks black or white text for a background colour, whichever gives the higher WCAG contrast
    /// ratio.
    /// </summary>
    /// <param name="hexColor">A validated three or six digit hexadecimal literal.</param>
    /// <returns><c>#000000</c> or <c>#ffffff</c>.</returns>
    private static string ContrastingInk(string hexColor)
    {
        (double r, double g, double b) = ParseChannels(hexColor);

        // WCAG 2.2 relative luminance, with the sRGB transfer function applied per channel.
        double luminance = (0.2126 * Linearize(r)) + (0.7152 * Linearize(g)) + (0.0722 * Linearize(b));

        // Contrast against white is (1.05)/(L+0.05); against black it is (L+0.05)/0.05. They cross at
        // L = sqrt(1.05 * 0.05) - 0.05, which is where the better choice flips.
        const double crossover = 0.179;

        return luminance > crossover ? "#000000" : "#ffffff";
    }

    private static (double R, double G, double B) ParseChannels(string hexColor)
    {
        ReadOnlySpan<char> digits = hexColor.AsSpan(1);

        if (digits.Length == 3)
        {
            return (
                (Hex(digits[0]) * 17) / 255.0,
                (Hex(digits[1]) * 17) / 255.0,
                (Hex(digits[2]) * 17) / 255.0);
        }

        return (
            ((Hex(digits[0]) * 16) + Hex(digits[1])) / 255.0,
            ((Hex(digits[2]) * 16) + Hex(digits[3])) / 255.0,
            ((Hex(digits[4]) * 16) + Hex(digits[5])) / 255.0);
    }

    /// <summary>Value of one hexadecimal digit. The input has already been validated.</summary>
    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };

    private static double Linearize(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
