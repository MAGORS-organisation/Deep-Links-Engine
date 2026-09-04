using Microsoft.Extensions.Caching.Hybrid;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Drops cached link data after a control plane change, using <c>HybridCache</c> tags (ADR-005).
/// </summary>
/// <remarks>
/// <para>
/// Tag based removal is what makes the two coarse invalidations possible. Neither a domain edit nor a
/// consent mode change knows which slugs are currently cached, and enumerating them would mean a
/// database query per invalidation on data the cache exists to avoid reading.
/// </para>
/// <para>
/// Removal reaches both levels: the shared L2 entry and, on every instance, the in-process L1 copy.
/// Dropping only the shared level is the classic half-fix — each process keeps serving its own stale
/// snapshot until its local entry expires, so the change appears on some replicas and not others.
/// </para>
/// <para>
/// Hosts are normalised on the way in. The control plane may hold a host as the operator typed it,
/// while the cache is keyed by the normalised form, and an invalidation that silently matches nothing
/// is worse than one that fails loudly.
/// </para>
/// </remarks>
public sealed class LinkCacheInvalidator : ILinkCacheInvalidator
{
    private readonly HybridCache _cache;

    /// <summary>Creates the invalidator.</summary>
    /// <param name="cache">The two-level cache.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is <see langword="null"/>.</exception>
    public LinkCacheInvalidator(HybridCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
    }

    /// <inheritdoc />
    public ValueTask InvalidateLinkAsync(string host, string slug, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        string normalizedHost = NormalizeHost(host);
        string normalizedSlug = SlugPolicy.TryNormalize(slug, out string parsed) ? parsed : slug;

        return _cache.RemoveAsync(LinkCacheKeys.Link(normalizedHost, normalizedSlug), ct);
    }

    /// <inheritdoc />
    public ValueTask InvalidateHostAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalizedHost = NormalizeHost(host);

        // The tag covers every link of the host; the three named keys are removed as well so that a
        // well-known or branding change takes effect even for entries an earlier version cached
        // without tags.
        return RemoveHostAsync(normalizedHost, ct);
    }

    /// <inheritdoc />
    public ValueTask InvalidateTenantAsync(Guid tenantId, CancellationToken ct) =>
        _cache.RemoveByTagAsync(LinkCacheKeys.TenantTag(tenantId), ct);

    private async ValueTask RemoveHostAsync(string host, CancellationToken ct)
    {
        await _cache.RemoveByTagAsync(LinkCacheKeys.HostTag(host), ct);

        await _cache.RemoveAsync(
            [
                LinkCacheKeys.Domain(host),
                LinkCacheKeys.Aasa(host),
                LinkCacheKeys.AssetLinks(host),
            ],
            ct);
    }

    private static string NormalizeHost(string host) =>
        HostNormalizer.TryNormalize(host, out string normalized) ? normalized : host;
}
