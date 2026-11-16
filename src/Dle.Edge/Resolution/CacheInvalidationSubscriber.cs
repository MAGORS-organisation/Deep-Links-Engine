using System.Text.Json;
using System.Threading.Channels;

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

        Channel<CacheInvalidationMessage> queue = Channel.CreateBounded<CacheInvalidationMessage>(
            new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        Task consumerTask = DrainQueueAsync(queue.Reader, stoppingToken);

        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            ISubscriber? subscriber = null;
            bool subscribed = false;

            try
            {
                subscriber = _redis.GetSubscriber();

                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(LinkCacheInvalidator.InvalidationChannel),
                    (_, message) =>
                    {
                        if (message.IsNullOrEmpty)
                        {
                            return;
                        }

                        try
                        {
                            CacheInvalidationMessage? msg = JsonSerializer.Deserialize(
                                (string)message!,
                                FastPersistenceJsonContext.Default.CacheInvalidationMessage);

                            if (msg is not null && !queue.Writer.TryWrite(msg))
                            {
                                _logger.LogWarning("Cache invalidation queue full; dropped message.");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Ignoring malformed cache invalidation payload from pub/sub");
                        }
                    });

                subscribed = true;
                attempt = 0;

                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (stoppingToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), disconnected))
                {
                    await disconnected.Task;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                attempt++;
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));
                _logger.LogWarning(
                    ex,
                    "Cache invalidation subscriber connection failed; retrying in {DelaySeconds}s.",
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                if (subscribed && subscriber is not null)
                {
                    try
                    {
                        await subscriber.UnsubscribeAsync(RedisChannel.Literal(LinkCacheInvalidator.InvalidationChannel));
                    }
                    catch
                    {
                        // Best-effort unsubscribe during reconnect or shutdown.
                    }
                }
            }
        }

        queue.Writer.TryComplete();
        await consumerTask;
    }

    private async Task DrainQueueAsync(ChannelReader<CacheInvalidationMessage> reader, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (CacheInvalidationMessage msg in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (string.Equals(msg.Type, "link", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(msg.Host)
                        && !string.IsNullOrEmpty(msg.Slug))
                    {
                        await _cache.RemoveAsync(LinkCacheKeys.Link(msg.Host, msg.Slug), stoppingToken);
                    }
                    else if (string.Equals(msg.Type, "host", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(msg.Host))
                    {
                        await _cache.RemoveByTagAsync(LinkCacheKeys.HostTag(msg.Host), stoppingToken);
                        await _cache.RemoveAsync(
                            [
                                LinkCacheKeys.Domain(msg.Host),
                                LinkCacheKeys.Aasa(msg.Host),
                                LinkCacheKeys.AssetLinks(msg.Host),
                            ],
                            stoppingToken);
                    }
                    else if (string.Equals(msg.Type, "tenant", StringComparison.Ordinal)
                        && msg.TenantId.HasValue)
                    {
                        await _cache.RemoveByTagAsync(LinkCacheKeys.TenantTag(msg.TenantId.Value), stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply cache invalidation message from pub/sub");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
