using System.Text.Json;

using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Dle.Edge.Resolution;

/// <summary>
/// Background service that subscribes to Valkey pub/sub channel <c>dle:cache:invalidation</c>
/// and invalidates local L1 cache entries across edge replicas (ADR-005).
/// </summary>
public sealed class CacheInvalidationSubscriber : BackgroundService
{
    private readonly HybridCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<CacheInvalidationSubscriber> _logger;

    /// <summary>Creates the subscriber.</summary>
    public CacheInvalidationSubscriber(
        HybridCache cache,
        ILogger<CacheInvalidationSubscriber> logger,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _logger = logger;
        _redis = redis;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_redis is null)
        {
            return;
        }

        try
        {
            ISubscriber subscriber = _redis.GetSubscriber();

            await subscriber.SubscribeAsync(
                RedisChannel.Literal(LinkCacheInvalidator.InvalidationChannel),
                (redisChannel, message) =>
                {
                    if (message.IsNullOrEmpty)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            CacheInvalidationMessage? msg = JsonSerializer.Deserialize(
                                (string)message!,
                                FastPersistenceJsonContext.Default.CacheInvalidationMessage);

                            if (msg is null)
                            {
                                return;
                            }

                            if (string.Equals(msg.Type, "link", StringComparison.Ordinal) &&
                                !string.IsNullOrEmpty(msg.Host) &&
                                !string.IsNullOrEmpty(msg.Slug))
                            {
                                await _cache.RemoveAsync(LinkCacheKeys.Link(msg.Host, msg.Slug));
                            }
                            else if (string.Equals(msg.Type, "host", StringComparison.Ordinal) &&
                                     !string.IsNullOrEmpty(msg.Host))
                            {
                                await _cache.RemoveByTagAsync(LinkCacheKeys.HostTag(msg.Host));
                                await _cache.RemoveAsync(
                                [
                                    LinkCacheKeys.Domain(msg.Host),
                                    LinkCacheKeys.Aasa(msg.Host),
                                    LinkCacheKeys.AssetLinks(msg.Host),
                                ]);
                            }
                            else if (string.Equals(msg.Type, "tenant", StringComparison.Ordinal) &&
                                     msg.TenantId.HasValue)
                            {
                                await _cache.RemoveByTagAsync(LinkCacheKeys.TenantTag(msg.TenantId.Value));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to apply cache invalidation message from pub/sub");
                        }
                    });
                });

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (stoppingToken.Register(state => ((TaskCompletionSource)state!).TrySetResult(), tcs))
            {
                await tcs.Task;
            }

            try
            {
                await subscriber.UnsubscribeAsync(RedisChannel.Literal(LinkCacheInvalidator.InvalidationChannel));
            }
            catch
            {
                // Unsubscribe on best-effort during shutdown
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache invalidation subscriber could not subscribe to pub/sub");
        }
    }
}
