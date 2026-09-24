using System.Net;

namespace Dle.Domain.Ports;

/// <summary>
/// Confirms that a client claiming to be a known crawler really is one, by reverse DNS with a
/// forward confirmation (FR-161, TC-107).
/// </summary>
/// <remarks>
/// The user agent string is free text and anyone can claim to be Googlebot. An unverified claim
/// must not earn the crawler treatment, because that treatment skips campaign statistics and
/// returns a preview page instead of a redirect — which would otherwise be a trivial way to make
/// clicks disappear from a competitor's report. A failed check is recorded as a spoofed bot and
/// the client is served as an ordinary one.
/// </remarks>
public interface IBotVerifier
{
    /// <summary>Verifies a crawler claim.</summary>
    /// <param name="crawlerName">Name of the crawler as recognised from the user agent.</param>
    /// <param name="address">Remote address of the client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> only when reverse DNS resolves to a domain the crawler
    /// owns and the forward lookup of that name returns the same address. Timeouts and errors
    /// return <see langword="false"/>: the check fails closed.</returns>
    ValueTask<bool> IsGenuineAsync(string crawlerName, IPAddress? address, CancellationToken ct);
}
