using Dle.Domain.Abuse;

namespace Dle.Domain.Ports;

/// <summary>
/// Full safety check of a link target: the offline rules of <see cref="TargetUrlPolicy"/> plus the
/// steps that need the network — resolving the host and checking every resulting address, and
/// consulting the configured reputation sources (§E.3, FR-244).
/// </summary>
/// <remarks>
/// It runs when a link is created and again on the nightly re-check, because changing the target
/// after approval is the oldest trick in the trade (§E.3, point 5).
/// </remarks>
public interface IUrlSafetyChecker
{
    /// <summary>Checks one target URL.</summary>
    /// <param name="url">The target to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verdict. A reputation source that is unreachable yields
    /// <see cref="UrlSafetyLevel.Unknown"/> rather than <see cref="UrlSafetyLevel.Safe"/>, so an
    /// outage in a feed can never silently turn into a pass.</returns>
    ValueTask<UrlSafetyVerdict> CheckAsync(string url, CancellationToken ct);
}
