using System.Text.Json;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Drops cached link data after a control plane change, using <c>HybridCache</c> tags (ADR-005)
/// and broadcasts invalidations across edge nodes via Valkey Pub/Sub.
/// </summary>
public sealed class LinkCacheInvalidator : ILinkCacheInvalidator
{
    /// <summary>The Pub/Sub channel used for cache invalidation events.</summary>
    public const string InvalidationChannel = "dle:cache:invalidation";

    private readonly HybridCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LinkCacheInvalidator>? _logger;

    /// <summary>Creates the invalidator.</summary>
    /// <param name="cache">The two-level cache.</param>
    /// <param name="redis">Optional Redis/Valkey connection multiplexer for Pub/Sub invalidations.</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is <see langword="null"/>.</exception>
    public LinkCacheInvalidator(
        HybridCache cache,
        IConnectionMultiplexer? redis = null,
        ILogger<LinkCacheInvalidator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask InvalidateLinkAsync(string host, string slug, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        string normalizedHost = NormalizeHost(host);
        string normalizedSlug = SlugPolicy.TryNormalize(slug, out string parsed) ? parsed : slug;

        await _cache.RemoveAsync(LinkCacheKeys.Link(normalizedHost, normalizedSlug), ct);
        await PublishAsync(new CacheInvalidationMessage("link", Host: normalizedHost, Slug: normalizedSlug), ct);
    }

    /// <inheritdoc />
    public async ValueTask InvalidateHostAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalizedHost = NormalizeHost(host);

        await RemoveHostAsync(normalizedHost, ct);
        await PublishAsync(new CacheInvalidationMessage("host", Host: normalizedHost), ct);
    }

    /// <inheritdoc />
    public async ValueTask InvalidateTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await _cache.RemoveByTagAsync(LinkCacheKeys.TenantTag(tenantId), ct);
        await PublishAsync(new CacheInvalidationMessage("tenant", TenantId: tenantId), ct);
    }

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

    private async ValueTask PublishAsync(CacheInvalidationMessage message, CancellationToken ct)
    {
        if (_redis is null || !_redis.IsConnected)
        {
            return;
        }

        try
        {
            string payload = JsonSerializer.Serialize(message, FastPersistenceJsonContext.Default.CacheInvalidationMessage);
            ISubscriber subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(InvalidationChannel), payload);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Failed to publish cache invalidation message for {Type} to {Channel}", message.Type, InvalidationChannel);
        }
    }

    private static string NormalizeHost(string host) =>
        HostNormalizer.TryNormalize(host, out string normalized) ? normalized : host;
}
