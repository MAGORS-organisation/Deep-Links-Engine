using Dle.Domain.WellKnown;

namespace Dle.Domain.Ports;

/// <summary>
/// Serves per-domain configuration and the two platform association files (FR-141, FR-142).
/// </summary>
public interface IDomainConfigStore
{
    /// <summary>Builds the Apple app site association document for a host.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The document, or <see langword="null"/> when no iOS application is registered for
    /// the host. The caller must answer 404 in that case: Apple caches an empty document for a
    /// week, so serving empty JSON breaks the domain for longer than a missing file does
    /// (TC-122).</returns>
    ValueTask<WellKnownDocument?> BuildAasaAsync(string host, CancellationToken ct);

    /// <summary>Builds the Digital Asset Links document for a host.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The document, or <see langword="null"/> when no Android application is registered
    /// for the host.</returns>
    ValueTask<WellKnownDocument?> BuildAssetLinksAsync(string host, CancellationToken ct);

    /// <summary>Loads the runtime configuration of a domain.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The configuration, or <see langword="null"/> for an unknown host.</returns>
    ValueTask<DomainRuntimeConfig?> GetDomainAsync(string host, CancellationToken ct);
}
