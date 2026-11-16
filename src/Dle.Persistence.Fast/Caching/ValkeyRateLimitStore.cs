using System.Globalization;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Outcome of an atomic rate limit check in Valkey.
/// </summary>
/// <param name="IsAcquired"><see langword="true"/> when the permit(s) were granted.</param>
/// <param name="Remaining">Approximate permits left in the current window or bucket.</param>
/// <param name="RetryAfter">Suggested wait duration before retrying when denied.</param>
public readonly record struct ValkeyRateLimitDecision(
    bool IsAcquired,
    long Remaining,
    TimeSpan? RetryAfter);

/// <summary>
/// Shared distributed rate-limit and shadow-ban store backed by Valkey atomic Lua scripts
/// (<see cref="IConnectionMultiplexer"/>), with automatic circuit-breaker fallback to in-process
/// limiters when Valkey is unconfigured, disconnected, or failing.
/// </summary>
public sealed partial class ValkeyRateLimitStore
{
    internal const string KeyPrefix = "dle:rl:";
    internal const string BanKeyPrefix = "dle:rl:ban:";

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    private const string FixedWindowScript = """
        local limit = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        local cost = tonumber(ARGV[3])
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        if current + cost > limit then
          local pttl = redis.call('PTTL', KEYS[1])
          if pttl < 0 then pttl = windowMs end
          return { 0, math.max(0, limit - current), math.max(1000, pttl) }
        end
        local updated = redis.call('INCRBY', KEYS[1], cost)
        if updated == cost then
          redis.call('PEXPIRE', KEYS[1], windowMs)
        else
          local pttl = redis.call('PTTL', KEYS[1])
          if pttl < 0 then redis.call('PEXPIRE', KEYS[1], windowMs) end
        end
        local pttl = redis.call('PTTL', KEYS[1])
        return { 1, math.max(0, limit - updated), math.max(0, pttl) }
        """;

    private const string SlidingWindowScript = """
        local t = redis.call('TIME')
        local nowMs = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local limit = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        local segments = math.max(1, tonumber(ARGV[3]))
        local cost = tonumber(ARGV[4])
        local segMs = math.max(1, math.floor(windowMs / segments))
        local curBucket = math.floor(nowMs / segMs)
        local minBucket = curBucket - segments + 1
        local fields = redis.call('HGETALL', KEYS[1])
        local total = 0
        local oldestBucket = curBucket
        for i = 1, #fields, 2 do
          local b = tonumber(fields[i])
          local c = tonumber(fields[i + 1])
          if b < minBucket then
            redis.call('HDEL', KEYS[1], fields[i])
          else
            total = total + c
            if b < oldestBucket then oldestBucket = b end
          end
        end
        if total + cost > limit then
          local expireBucket = oldestBucket + segments
          local retryMs = math.max(1000, expireBucket * segMs - nowMs)
          return { 0, math.max(0, limit - total), retryMs }
        end
        redis.call('HINCRBY', KEYS[1], tostring(curBucket), cost)
        redis.call('PEXPIRE', KEYS[1], windowMs + segMs)
        return { 1, math.max(0, limit - total - cost), 0 }
        """;

    private const string TokenBucketScript = """
        local t = redis.call('TIME')
        local nowMs = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local burst = tonumber(ARGV[1])
        local tokensPerPeriod = tonumber(ARGV[2])
        local periodMs = math.max(1, tonumber(ARGV[3]))
        local cost = tonumber(ARGV[4])
        local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(data[1])
        local lastTs = tonumber(data[2])
        if tokens == nil or lastTs == nil then
          tokens = burst
          lastTs = nowMs
        else
          local elapsed = math.max(0, nowMs - lastTs)
          if elapsed > 0 and tokens < burst then
            local periods = math.floor(elapsed / periodMs)
            if periods > 0 then
              tokens = math.min(burst, tokens + periods * tokensPerPeriod)
              lastTs = lastTs + periods * periodMs
            end
          elseif tokens >= burst then
            lastTs = nowMs
          end
        end
        local ttlMs = math.max(periodMs * 2, math.ceil(burst / math.max(1, tokensPerPeriod)) * periodMs)
        if tokens < cost then
          local missing = cost - tokens
          local periodsNeeded = math.ceil(missing / math.max(1, tokensPerPeriod))
          local retryMs = math.max(1000, lastTs + periodsNeeded * periodMs - nowMs)
          redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', lastTs)
          redis.call('PEXPIRE', KEYS[1], ttlMs)
          return { 0, tokens, retryMs }
        end
        tokens = tokens - cost
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', lastTs)
        redis.call('PEXPIRE', KEYS[1], ttlMs)
        return { 1, tokens, 0 }
        """;

    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValkeyRateLimitStore> _logger;
    private long _backoffUntilTicks;

    /// <summary>
    /// Creates a Valkey rate-limit store.
    /// </summary>
    /// <param name="timeProvider">Clock used for circuit-breaker backoff recovery.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="redis">Optional Valkey connection multiplexer; when absent, callers use local limiters.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public ValkeyRateLimitStore(
        TimeProvider timeProvider,
        ILogger<ValkeyRateLimitStore> logger,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _logger = logger;
        _redis = redis;
    }

    /// <summary>
    /// Whether Valkey is configured, connected, and not inside a transient failure backoff window.
    /// </summary>
    public bool IsAvailable =>
        _redis is not null
        && _redis.IsConnected
        && Volatile.Read(ref _backoffUntilTicks) <= _timeProvider.GetUtcNow().UtcTicks;

    /// <summary>Attempts to acquire permits from a distributed fixed-window counter.</summary>
    public ValueTask<ValkeyRateLimitDecision?> TryAcquireFixedWindowAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        int cost = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);

        long windowMs = Math.Max(1, (long)Math.Ceiling(window.TotalMilliseconds));

        return EvaluateScriptAsync(
            FixedWindowScript,
            KeyPrefix + partitionKey,
            [permitLimit, windowMs, cost],
            cancellationToken);
    }

    /// <summary>Reads the current integer counter of a fixed-window key without consuming a permit.</summary>
    public async ValueTask<long?> GetCounterAsync(
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            IDatabase db = _redis!.GetDatabase();
            RedisValue value = await db.StringGetAsync(KeyPrefix + partitionKey).ConfigureAwait(false);

            if (value.IsNullOrEmpty)
            {
                return 0L;
            }

            return long.TryParse((string?)value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count)
                ? count
                : 0L;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            TripBackoff(ex);
            return null;
        }
    }

    /// <summary>Attempts to acquire permits from a distributed segmented sliding window.</summary>
    public ValueTask<ValkeyRateLimitDecision?> TryAcquireSlidingWindowAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        int segments,
        int cost = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);

        long windowMs = Math.Max(1, (long)Math.Ceiling(window.TotalMilliseconds));

        return EvaluateScriptAsync(
            SlidingWindowScript,
            KeyPrefix + partitionKey,
            [permitLimit, windowMs, segments, cost],
            cancellationToken);
    }

    /// <summary>Attempts to acquire permits from a distributed token bucket.</summary>
    public ValueTask<ValkeyRateLimitDecision?> TryAcquireTokenBucketAsync(
        string partitionKey,
        int burst,
        int tokensPerPeriod,
        TimeSpan period,
        int cost = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);

        long periodMs = Math.Max(1, (long)Math.Ceiling(period.TotalMilliseconds));

        return EvaluateScriptAsync(
            TokenBucketScript,
            KeyPrefix + partitionKey,
            [burst, tokensPerPeriod, periodMs, cost],
            cancellationToken);
    }

    /// <summary>Checks whether a ban key is active in Valkey.</summary>
    public async ValueTask<bool?> IsBannedAsync(
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);

        if (cancellationToken.IsCancellationRequested || !IsAvailable)
        {
            return null;
        }

        try
        {
            IDatabase db = _redis!.GetDatabase();
            RedisValue value = await db.StringGetAsync(BanKeyPrefix + partitionKey).ConfigureAwait(false);
            return !value.IsNull;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            TripBackoff(ex);
            return null;
        }
    }

    /// <summary>Stores a distributed ban key in Valkey with the specified TTL.</summary>
    public async ValueTask SetBanAsync(
        string partitionKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);

        if (duration <= TimeSpan.Zero || cancellationToken.IsCancellationRequested || !IsAvailable)
        {
            return;
        }

        try
        {
            IDatabase db = _redis!.GetDatabase();
            _ = await db.StringSetAsync(BanKeyPrefix + partitionKey, "1", duration).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            TripBackoff(ex);
        }
    }

    private async ValueTask<ValkeyRateLimitDecision?> EvaluateScriptAsync(
        string script,
        string fullKey,
        RedisValue[] arguments,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !IsAvailable)
        {
            return null;
        }

        try
        {
            IDatabase db = _redis!.GetDatabase();
            RedisResult raw = await db.ScriptEvaluateAsync(script, [fullKey], arguments).ConfigureAwait(false);
            return ParseDecision(raw);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            TripBackoff(ex);
            return null;
        }
    }

    internal static ValkeyRateLimitDecision? ParseDecision(RedisResult raw)
    {
        if (raw.IsNull)
        {
            return null;
        }

        RedisResult[]? parts = (RedisResult[]?)raw;
        if (parts is not { Length: >= 3 })
        {
            return null;
        }

        bool allowed = (long)parts[0] != 0L;
        long remaining = Math.Max(0L, (long)parts[1]);
        long retryMs = Math.Max(0L, (long)parts[2]);

        TimeSpan? retryAfter = allowed
            ? null
            : retryMs > 0
                ? TimeSpan.FromMilliseconds(retryMs)
                : TimeSpan.FromSeconds(1);

        return new ValkeyRateLimitDecision(allowed, remaining, retryAfter);
    }

    private void TripBackoff(Exception ex)
    {
        long nextAttempt = _timeProvider.GetUtcNow().Add(FailureBackoff).UtcTicks;
        Interlocked.Exchange(ref _backoffUntilTicks, nextAttempt);
        ValkeyFallbackActive(_logger, ex);
    }

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Warning,
        Message = "Valkey rate-limit store failed; falling back to local in-memory rate limiting for 5 seconds.")]
    private static partial void ValkeyFallbackActive(ILogger logger, Exception ex);
}
