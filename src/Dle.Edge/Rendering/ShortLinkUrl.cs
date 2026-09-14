namespace Dle.Edge.Rendering;

/// <summary>
/// Builds the canonical form of a short link, <c>https://host/slug</c>.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, because two would eventually disagree and the two consumers are exactly the two
/// places where disagreement is expensive: the crawler preview's <c>og:url</c>, where a wrong value
/// attributes the preview to the wrong page, and the QR payload, where a wrong value is printed on
/// something nobody can recall.
/// </para>
/// <para>
/// The scheme is always <c>https</c> and is never taken from the request. A link domain has to serve
/// TLS to work at all — Apple fetches the association file over HTTPS only (§A.2.1) — so a request that
/// arrived as plain HTTP, whether from a stray client or through a proxy that did not set the forwarded
/// header, still yields the address the link actually lives at.
/// </para>
/// </remarks>
internal static class ShortLinkUrl
{
    /// <summary>The fixed scheme of every short link.</summary>
    internal const string Scheme = "https://";

    /// <summary>
    /// Builds <c>https://host/slug</c>.
    /// </summary>
    /// <param name="host">A host already normalized by <c>HostNormalizer</c>.</param>
    /// <param name="slug">A slug already normalized by <c>SlugPolicy</c>.</param>
    /// <returns>The canonical URL, or <see langword="null"/> when either part is missing.</returns>
    internal static string? Build(string? host, string? slug) =>
        string.IsNullOrEmpty(host) || string.IsNullOrEmpty(slug)
            ? null
            : string.Concat(Scheme, host, "/", slug);
}
