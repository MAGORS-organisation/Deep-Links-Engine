using System.Diagnostics.CodeAnalysis;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// A URL reported by a <c>link_open</c> event, reduced to the two values that identify one of our
/// links.
/// </summary>
/// <param name="Host">Normalized host, as <see cref="HostNormalizer"/> spells it.</param>
/// <param name="Slug">Normalized slug, as <see cref="SlugPolicy"/> spells it.</param>
public readonly record struct ReportedLink(string Host, string Slug);

/// <summary>
/// Turns the URL an application says it was opened with into a host and a slug (§B.6.4, FR-223).
/// </summary>
/// <remarks>
/// <para>
/// The input is attacker controlled: it arrives in a JSON body from a binary that anybody can
/// modify. Nothing is echoed back, nothing is logged whole, and only the two normalized components
/// survive — the query string and the fragment are discarded here rather than downstream, because
/// a value that never leaves this method cannot be logged by accident (SHARED-KERNEL §17.5).
/// </para>
/// <para>
/// Discarding the query is also correct on the merits. The only thing the reported URL is used for
/// is to look up which of the tenant's links was opened; the deep link path and the parameters
/// come back from the stored link, never from what the caller sent (SHARED-KERNEL §17.4).
/// </para>
/// </remarks>
public static class LinkUrlParser
{
    /// <summary>Longest URL this parser will look at, matching the target URL policy.</summary>
    public const int MaxUrlLength = TargetUrlPolicy.MaxUrlLength;

    /// <summary>
    /// Parses a reported URL.
    /// </summary>
    /// <param name="url">The URL as reported. Untrusted.</param>
    /// <param name="reported">The host and slug when the URL is shaped like one of our links.</param>
    /// <returns>
    /// <see langword="true"/> when the URL is an absolute <c>http</c> or <c>https</c> URL whose
    /// first path segment is a syntactically valid slug. It says nothing about whether such a link
    /// exists: that is a database question and the answer to it is tenant scoped.
    /// </returns>
    public static bool TryParse(string? url, out ReportedLink reported)
    {
        reported = default;

        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (!IsHttp(parsed.Scheme))
        {
            // A custom scheme open (myapp://…) is a legitimate event, but it carries no host and no
            // slug of ours, so there is nothing here to resolve.
            return false;
        }

        if (!HostNormalizer.TryNormalize(parsed.Host, out string host))
        {
            return false;
        }

        if (!TryReadFirstSegment(parsed.AbsolutePath, out string? segment))
        {
            return false;
        }

        if (!SlugPolicy.TryNormalize(segment, out string slug) || SlugPolicy.IsReserved(slug))
        {
            return false;
        }

        reported = new ReportedLink(host, slug);
        return true;
    }

    /// <summary>Whether a scheme is one a short link can be served over.</summary>
    private static bool IsHttp(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the first non-empty path segment, percent decoded.</summary>
    /// <param name="absolutePath">The path component of the URL.</param>
    /// <param name="segment">The first segment when there is one.</param>
    /// <returns><see langword="true"/> when a segment was found.</returns>
    private static bool TryReadFirstSegment(string absolutePath, [NotNullWhen(true)] out string? segment)
    {
        segment = null;

        ReadOnlySpan<char> path = absolutePath.AsSpan().TrimStart('/');

        if (path.IsEmpty)
        {
            return false;
        }

        int end = path.IndexOf('/');
        ReadOnlySpan<char> first = end < 0 ? path : path[..end];

        if (first.IsEmpty || first.Length > SlugPolicy.MaxLength * 3)
        {
            // The bound is generous because a percent encoded slug is at most three characters per
            // byte; anything longer cannot normalize to a slug and is refused before decoding.
            return false;
        }

        segment = Uri.UnescapeDataString(first.ToString());
        return segment.Length > 0;
    }
}
