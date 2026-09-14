using Dle.Domain.Links;

namespace Dle.Domain.Ports;

/// <summary>
/// The single lookup on the resolve hot path: host and slug to link snapshot.
/// </summary>
/// <remarks>
/// Implementations are expected to answer from the two level cache and to fall back to at most
/// one database query, because the latency budget of §B.6.1 allows nothing else. A miss must be
/// cached as well: an enumeration attack is mostly a stream of misses, and re-querying the
/// database for each one is how the attack becomes a denial of service.
/// </remarks>
public interface ILinkStore
{
    /// <summary>Finds the link served for a host and slug.</summary>
    /// <param name="host">Normalized host of the request.</param>
    /// <param name="slug">Normalized slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot, or <see langword="null"/> when no link exists. A quarantined or
    /// expired link is still returned: the caller needs it to answer 410 rather than 404
    /// (TC-103), and that decision belongs to <c>LinkSnapshot.GetServeState</c>, not here.</returns>
    ValueTask<LinkSnapshot?> FindAsync(string host, string slug, CancellationToken ct);
}
