namespace Dle.Edge.Rendering;

/// <summary>
/// Image formats <c>GET /{slug}/qr</c> can answer with (FR-106).
/// </summary>
/// <remarks>
/// Two, and only two. A vector format for anything that will be printed or scaled, and a raster format
/// for the places that still refuse an SVG — a slide deck, an email client, a social upload form. Both
/// are produced without an imaging library and without a temporary file.
/// </remarks>
internal enum QrFormat
{
    /// <summary>Scalable vector graphics. The default, and the only format that carries a centre logo.</summary>
    Svg = 0,

    /// <summary>An indexed PNG rendered at a whole number of pixels per module.</summary>
    Png = 1,
}
