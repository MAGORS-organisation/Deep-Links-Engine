namespace Dle.Domain.Ports;

/// <summary>
/// Drops cached link data after a control plane change, at three granularities (ADR-005).
/// </summary>
/// <remarks>
/// The edge answers from a two level cache, so a control plane write is not visible until the
/// caches are told. Both levels must be dropped, on every instance: invalidating only the shared
/// level leaves each process serving its own stale copy for the rest of its local lifetime.
/// </remarks>
public interface ILinkCacheInvalidator
{
    /// <summary>Invalidates one link.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="slug">Normalized slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the invalidation has been published.</returns>
    ValueTask InvalidateLinkAsync(string host, string slug, CancellationToken ct);

    /// <summary>Invalidates every entry of one host, used when domain configuration or the
    /// well-known files change (TC-125).</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the invalidation has been published.</returns>
    ValueTask InvalidateHostAsync(string host, CancellationToken ct);

    /// <summary>Invalidates everything belonging to a tenant, used when the consent mode or the
    /// tenant status changes.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the invalidation has been published.</returns>
    ValueTask InvalidateTenantAsync(Guid tenantId, CancellationToken ct);
}
