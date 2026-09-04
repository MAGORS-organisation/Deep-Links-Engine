using System.Collections;
using System.Text;

using QRCoder;

using static QRCoder.QRCodeGenerator;

namespace Dle.Edge.Rendering;

/// <summary>
/// Renders a short link as a QR code, in SVG or PNG, with a configurable error correction level and an
/// optional centre logo (FR-106).
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoder is QRCoder; the drawing is ours.</b> Producing a correct symbol — mode selection,
/// version choice, Reed–Solomon words, masking — is the part that is genuinely hard and is worth a
/// dependency. Turning the resulting module matrix into markup is three loops, and doing it here buys
/// three things the library's own SVG writer does not offer: an accessible name on the image, a
/// document with a <c>viewBox</c> that scales cleanly at any requested size, and certainty about every
/// byte that ends up in a document served from our own origin.
/// </para>
/// <para>
/// <b>Colours are fixed.</b> Tenant branding does not reach the modules. A QR code is read by a camera
/// under whatever light the room has, and a low-contrast pair — which is exactly what a brand palette
/// eventually produces — fails to scan for reasons the tenant will never be able to reproduce. Branding
/// is expressed through the centre logo instead, which the error correction is designed to absorb.
/// </para>
/// <para>
/// <b>The logo is SVG only, and it is embedded.</b> It arrives as a base64 <c>data:</c> URI validated by
/// <see cref="SafeUrl.ImageDataUri(string?)"/>. It cannot be an external URL: a document served as an
/// image may not load subresources, and fetching a tenant-supplied URL from the edge would be both a
/// third-party call on a cached path (NFR-14) and a server-side request forgery primitive. The PNG
/// carries no logo because compositing one would require decoding an arbitrary image format, and an
/// image codec is not a dependency this service is willing to expose to tenant-controlled bytes.
/// </para>
/// </remarks>
internal static class QrCodeRenderer
{
    /// <summary>Default rendered size in pixels.</summary>
    internal const int DefaultSize = 512;

    /// <summary>Smallest rendered size in pixels.</summary>
    internal const int MinSize = 64;

    /// <summary>
    /// Largest rendered size in pixels. A PNG at this size is under a megabyte, which is the point:
    /// the parameter is public and the endpoint is unauthenticated.
    /// </summary>
    internal const int MaxSize = 2048;

    /// <summary>Content type of the SVG response.</summary>
    internal const string SvgContentType = "image/svg+xml";

    /// <summary>Content type of the PNG response.</summary>
    internal const string PngContentType = "image/png";

    /// <summary>Foreground of every symbol.</summary>
    private const string DarkColor = "#000000";

    /// <summary>Background of every symbol. Never transparent: a scanner needs the quiet zone to be light.</summary>
    private const string LightColor = "#ffffff";

    /// <summary>Side of the centre logo as a fraction of the symbol, quiet zone excluded.</summary>
    private const double LogoFraction = 0.18;

    /// <summary>Modules of light padding drawn around the centre logo.</summary>
    private const int LogoPadding = 1;

    /// <summary>Largest pixels-per-module factor a PNG is rendered at.</summary>
    private const int MaxPixelsPerModule = 64;

    /// <summary>
    /// Parses the <c>format</c> query parameter. An absent value is <see cref="QrFormat.Svg"/>.
    /// </summary>
    /// <param name="value">The raw parameter.</param>
    /// <param name="format">The parsed format.</param>
    /// <returns><see langword="false"/> for a value that is present and not recognised.</returns>
    internal static bool TryParseFormat(string? value, out QrFormat format)
    {
        format = QrFormat.Svg;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("png", StringComparison.OrdinalIgnoreCase))
        {
            format = QrFormat.Png;
            return true;
        }

        // Unrecognised: refuse rather than silently substituting a format the caller did not ask for
        // (SHARED-KERNEL §17.9).
        return false;
    }

    /// <summary>
    /// Parses the <c>ecc</c> query parameter. An absent value is <see cref="ECCLevel.M"/>, the level
    /// that balances symbol density against print and screen damage for a URL of this length.
    /// </summary>
    /// <param name="value">The raw parameter.</param>
    /// <param name="level">The parsed level.</param>
    /// <returns><see langword="false"/> for a value that is present and not one of L, M, Q or H.</returns>
    internal static bool TryParseEcc(string? value, out ECCLevel level)
    {
        level = ECCLevel.M;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Length != 1)
        {
            return false;
        }

        switch (char.ToUpperInvariant(value[0]))
        {
            case 'L':
                level = ECCLevel.L;
                return true;
            case 'M':
                return true;
            case 'Q':
                level = ECCLevel.Q;
                return true;
            case 'H':
                level = ECCLevel.H;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Clamps the requested size into the supported range.</summary>
    /// <param name="size">The raw parameter, or <see langword="null"/>.</param>
    /// <returns>A size between <see cref="MinSize"/> and <see cref="MaxSize"/>.</returns>
    internal static int ClampSize(int? size) =>
        size is null ? DefaultSize : Math.Clamp(size.Value, MinSize, MaxSize);

    /// <summary>
    /// Renders one QR code.
    /// </summary>
    /// <param name="payload">The URL the symbol encodes. Built by the server, never taken from the query.</param>
    /// <param name="format">The image format.</param>
    /// <param name="size">The rendered size in pixels, already clamped.</param>
    /// <param name="ecc">The requested error correction level.</param>
    /// <param name="logoDataUri">
    /// A validated base64 image data URI drawn at the centre, or <see langword="null"/>. Honoured for
    /// <see cref="QrFormat.Svg"/> only.
    /// </param>
    /// <param name="accessibleName">The image's accessible name, written into the SVG title.</param>
    /// <returns>The encoded image.</returns>
    /// <remarks>
    /// A centre logo raises the level to at least <see cref="ECCLevel.Q"/>. The logo covers roughly
    /// three per cent of the symbol's area, which level M would survive on paper, but a printed code is
    /// also creased, glared and photographed at an angle, and the budget has to cover all of it at once.
    /// </remarks>
    internal static byte[] Render(
        string payload,
        QrFormat format,
        int size,
        ECCLevel ecc,
        string? logoDataUri,
        string accessibleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        bool withLogo = format is QrFormat.Svg && logoDataUri is not null;
        ECCLevel effective = withLogo && ecc is ECCLevel.L or ECCLevel.M ? ECCLevel.Q : ecc;

        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, effective);

        if (format is not QrFormat.Png)
        {
            return Encoding.UTF8.GetBytes(RenderSvg(data, size, withLogo ? logoDataUri : null, accessibleName));
        }

        using PngByteQRCode png = new(data);

        return png.GetGraphic(PixelsPerModule(data, size));
    }

    /// <summary>
    /// Chooses a whole number of pixels per module for a PNG.
    /// </summary>
    /// <remarks>
    /// Whole modules, always. A fractional scale would put module boundaries inside pixels, and a
    /// scanner reading a half-lit module is a scanner that reads nothing. The consequence is that the
    /// PNG is the largest whole multiple that fits the requested size rather than exactly that size,
    /// which is why the SVG is the default.
    /// </remarks>
    private static int PixelsPerModule(QRCodeData data, int size) =>
        Math.Clamp(size / Math.Max(1, data.ModuleMatrix.Count), 1, MaxPixelsPerModule);

    /// <summary>
    /// Writes the symbol as an SVG document.
    /// </summary>
    /// <remarks>
    /// The dark modules become a single <c>path</c> built from horizontal runs, which is a fraction of
    /// the size of one rectangle per module and parses faster in every renderer. <c>shape-rendering</c>
    /// is set to <c>crispEdges</c> so that no antialiasing softens a module boundary at small sizes.
    /// </remarks>
    private static string RenderSvg(QRCodeData data, int size, string? logoDataUri, string accessibleName)
    {
        List<BitArray> matrix = data.ModuleMatrix;
        int modules = matrix.Count;

        HtmlBuilder svg = new(8 * 1024);

        svg.Raw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Raw("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" role=\"img\" ")
            .Raw("aria-labelledby=\"dle-qr-title\" shape-rendering=\"crispEdges\" viewBox=\"0 0 ")
            .Number(modules)
            .Raw(" ")
            .Number(modules)
            .Raw("\"")
            .Attribute("width", size.ToString(CultureInfo.InvariantCulture))
            .Attribute("height", size.ToString(CultureInfo.InvariantCulture))
            .Raw(">\n<title id=\"dle-qr-title\">")
            .Text(accessibleName)
            .Raw("</title>\n")
            .Raw("<rect width=\"100%\" height=\"100%\" fill=\"" + LightColor + "\"/>\n")
            .Raw("<path fill=\"" + DarkColor + "\" d=\"");

        WriteModulePath(svg, matrix, modules);

        svg.Raw("\"/>\n");

        if (logoDataUri is not null)
        {
            WriteLogo(svg, modules, logoDataUri);
        }

        return svg.Raw("</svg>\n").ToString();
    }

    /// <summary>Emits one sub-path per run of dark modules.</summary>
    private static void WriteModulePath(HtmlBuilder svg, List<BitArray> matrix, int modules)
    {
        for (int y = 0; y < modules; y++)
        {
            BitArray row = matrix[y];
            int x = 0;

            while (x < modules)
            {
                if (!row[x])
                {
                    x++;
                    continue;
                }

                int start = x;

                while (x < modules && row[x])
                {
                    x++;
                }

                svg.Raw("M").Number(start).Raw(" ").Number(y)
                    .Raw("h").Number(x - start)
                    .Raw("v1h-").Number(x - start)
                    .Raw("z");
            }
        }
    }

    /// <summary>
    /// Draws the light plate and the tenant's mark at the centre of the symbol.
    /// </summary>
    /// <remarks>
    /// The plate is drawn even though the logo may be opaque, because a transparent PNG or an SVG with
    /// a transparent background would otherwise leave modules showing through the mark and turn a
    /// recoverable occlusion into unreadable noise.
    /// </remarks>
    private static void WriteLogo(HtmlBuilder svg, int modules, string logoDataUri)
    {
        // The quiet zone is four modules on each side and is part of the matrix; the symbol itself is
        // what the logo is sized against.
        const int quietZone = 4;

        int symbol = modules - (2 * quietZone);
        int logo = Math.Max(4, (int)Math.Round(symbol * LogoFraction, MidpointRounding.AwayFromZero));
        int plate = logo + (2 * LogoPadding);
        int plateOrigin = (modules - plate) / 2;
        int logoOrigin = (modules - logo) / 2;

        svg.Raw("<rect fill=\"" + LightColor + "\" rx=\"1\"")
            .Attribute("x", plateOrigin.ToString(CultureInfo.InvariantCulture))
            .Attribute("y", plateOrigin.ToString(CultureInfo.InvariantCulture))
            .Attribute("width", plate.ToString(CultureInfo.InvariantCulture))
            .Attribute("height", plate.ToString(CultureInfo.InvariantCulture))
            .Raw("/>\n")
            .Raw("<image preserveAspectRatio=\"xMidYMid meet\"")
            .Attribute("x", logoOrigin.ToString(CultureInfo.InvariantCulture))
            .Attribute("y", logoOrigin.ToString(CultureInfo.InvariantCulture))
            .Attribute("width", logo.ToString(CultureInfo.InvariantCulture))
            .Attribute("height", logo.ToString(CultureInfo.InvariantCulture))
            .Attribute("href", logoDataUri)
            .Raw("/>\n");
    }
}
