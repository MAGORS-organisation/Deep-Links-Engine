using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Shared single-use token replay guard backed by Valkey using atomic <c>SET ... NX PX</c>
/// semantics (§E.4.2).
/// </summary>
/// <remarks>
/// Ensures a single-use token consumed on one edge or control instance is refused on every other
/// instance. Fails closed (<see langword="false"/>) when the token is already expired, already
/// recorded, or when Valkey cannot be reached to persist the marker.
/// </remarks>
public sealed class ValkeyTokenReplayGuard : ITokenReplayGuard
{
    internal const string KeyPrefix = "dle:replay:jti:";

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValkeyTokenReplayGuard> _logger;

    /// <summary>
    /// Creates a Valkey-backed token replay guard.
    /// </summary>
    /// <param name="redis">Shared Valkey connection multiplexer.</param>
    /// <param name="timeProvider">Clock used to compute remaining TTL until token expiry.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ValkeyTokenReplayGuard(
        IConnectionMultiplexer redis,
        TimeProvider timeProvider,
        ILogger<ValkeyTokenReplayGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryConsumeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(jti);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan ttl = expiresAt - now;
        if (ttl <= TimeSpan.Zero)
        {
            return false;
        }

        if (!_redis.IsConnected)
        {
            _logger.LogWarning("Valkey is disconnected; refusing single-use token consumption fail-closed.");
            return false;
        }

        try
        {
            IDatabase db = _redis.GetDatabase();
            return await db.StringSetAsync(KeyPrefix + jti, "1", ttl, When.NotExists).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Valkey token replay guard operation failed; refusing single-use token fail-closed.");
            return false;
        }
    }
}
