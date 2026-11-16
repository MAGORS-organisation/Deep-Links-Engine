using StackExchange.Redis;

namespace Dle.Control.Identity;

/// <summary>
/// Subscribes to Valkey pub/sub channel <c>dle:credential:invalidation</c> and drops local
/// credential cache entries when another control-plane replica revokes a key, deletes an app, or
/// suspends or deletes a tenant.
/// </summary>
public sealed class CredentialCacheInvalidationSubscriber : BackgroundService
{
    private readonly DleCredentialCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<CredentialCacheInvalidationSubscriber> _logger;

    /// <summary>Creates the subscriber.</summary>
    /// <param name="cache">Local credential verification cache.</param>
    /// <param name="logger">Subscriber logger.</param>
    /// <param name="redis">Optional Valkey connection multiplexer.</param>
    public CredentialCacheInvalidationSubscriber(
        DleCredentialCache cache,
        ILogger<CredentialCacheInvalidationSubscriber> logger,
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

        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ISubscriber subscriber = _redis.GetSubscriber();

                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(DleCredentialCache.InvalidationChannel),
                    (_, message) =>
                    {
                        if (message.IsNullOrEmpty)
                        {
                            return;
                        }

                        ApplyMessage((string)message!);
                    });

                attempt = 0;
                await Task.Delay(Timeout.Infinite, stoppingToken);
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
                    "Credential cache invalidation subscriber connection failed; retrying in {DelaySeconds}s.",
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
        }
    }

    internal void ApplyMessage(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        int colon = payload.IndexOf(':');
        if (colon <= 0 || !Guid.TryParse(payload.AsSpan(colon + 1), out Guid id))
        {
            return;
        }

        ReadOnlySpan<char> kind = payload.AsSpan(0, colon);

        if (kind.Equals("key", StringComparison.Ordinal))
        {
            _cache.InvalidateKey(id);
        }
        else if (kind.Equals("tenant", StringComparison.Ordinal))
        {
            _cache.InvalidateTenant(id);
        }
        else if (kind.Equals("app", StringComparison.Ordinal))
        {
            _cache.InvalidateApp(id);
        }
    }
}
