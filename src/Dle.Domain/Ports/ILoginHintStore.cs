using Dle.Domain.Entities;

namespace Dle.Domain.Ports;

/// <summary>
/// Indexed lookup and persistence store for S2 login reconciliation hints (FR-185).
/// </summary>
public interface ILoginHintStore
{
    /// <summary>Persists a keyed login hint for later <c>O(1)</c> install reconciliation.</summary>
    /// <param name="hint">The hint record to store.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordAsync(ClickLoginHint hint, CancellationToken ct);

    /// <summary>
    /// Finds the most recent non-expired login hint for <paramref name="loginKeyHash"/> within the
    /// tenant and reconciliation window.
    /// </summary>
    /// <param name="tenantId">Tenant boundary.</param>
    /// <param name="loginKeyHash">Keyed hash produced by <c>LoginKeyHasher</c>.</param>
    /// <param name="minOccurredAt">Lower bound on <c>occurred_at</c>, in UTC.</param>
    /// <param name="now">Current UTC timestamp used to exclude expired hints.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newest matching hint, or <see langword="null"/> when none exists.</returns>
    ValueTask<ClickLoginHint?> FindLatestAsync(
        Guid tenantId,
        byte[] loginKeyHash,
        DateTimeOffset minOccurredAt,
        DateTimeOffset now,
        CancellationToken ct);
}
