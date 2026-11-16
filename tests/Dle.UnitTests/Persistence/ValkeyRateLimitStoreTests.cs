using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;

using Dle.Control.Configuration;
using Dle.Control.Features.Attribution;
using Dle.Control.Identity;
using Dle.Edge.RateLimiting;
using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Caching;
using Dle.UnitTests.Crypto;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Xunit;

namespace Dle.UnitTests.Persistence;

/// <summary>
/// Distributed Valkey rate limiting &amp; 404 shadow bans (<see cref="ValkeyRateLimitStore"/>), plus
/// automatic in-memory fallback when Valkey is null or throws transient errors (A4, §E.9).
/// </summary>
public sealed class ValkeyRateLimitStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NullMultiplexer_ReportsUnavailableAndFallsBackToInProcess()
    {
        var clock = new CryptoTestClock(Now);
        var store = new ValkeyRateLimitStore(clock, NullLogger<ValkeyRateLimitStore>.Instance, redis: null);

        Assert.False(store.IsAvailable);
        Assert.Null(await store.TryAcquireSlidingWindowAsync("edge:resolve:test", 10, TimeSpan.FromMinutes(1), 6, 1, TestContext.Current.CancellationToken));
        Assert.Null(await store.IsBannedAsync("nf:test", TestContext.Current.CancellationToken));

        using var authLimiter = new DleAuthAttemptLimiter(
            Options.Create(new DleRateLimitOptions { AuthAttemptBurst = 1, AuthAttemptsPerMinute = 1 }),
            clock,
            store);

        Assert.True(await authLimiter.TryAcquireAsync("198.51.100.0/24", "dle_a", TestContext.Current.CancellationToken));
        Assert.False(await authLimiter.TryAcquireAsync("198.51.100.0/24", "dle_a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValkeyFailure_TripsCircuitBreakerBackoffAndRecoversAfterFiveSeconds()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, MockRedisState state) = MockRedis.Create();
        var store = new ValkeyRateLimitStore(clock, NullLogger<ValkeyRateLimitStore>.Instance, redis);

        state.ExceptionToThrow = new TimeoutException("Valkey timed out.");
        Assert.Null(await store.TryAcquireFixedWindowAsync("attr:ccf:k1", 5, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.False(store.IsAvailable);

        int callsAtTrip = state.Calls;
        // Subsequent call during 5-second backoff skips Valkey immediately.
        Assert.Null(await store.TryAcquireFixedWindowAsync("attr:ccf:k1", 5, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.Equal(callsAtTrip, state.Calls);

        state.ExceptionToThrow = null;
        clock.Now = Now.AddSeconds(5);
        Assert.True(store.IsAvailable);

        ValkeyRateLimitDecision? decision = await store.TryAcquireFixedWindowAsync("attr:ccf:k1", 5, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken);
        Assert.NotNull(decision);
        Assert.True(decision.Value.IsAcquired);
        Assert.Equal(callsAtTrip + 1, state.Calls);
    }

    [Fact]
    public async Task NotFoundEnumerationGuard_SharesTokenBucketAndShadowBanAcrossReplicas()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, _) = MockRedis.Create();
        var store = new ValkeyRateLimitStore(clock, NullLogger<ValkeyRateLimitStore>.Instance, redis);

        IOptions<EdgeRateLimitOptions> options = Options.Create(new EdgeRateLimitOptions
        {
            NotFound = new NotFoundRateLimitOptions
            {
                Burst = 2,
                TokensPerPeriod = 2,
                ReplenishmentPeriodSeconds = 60,
                ShadowBanMinutes = 15,
            },
        });

        using var metrics = new EdgeMetrics(meterFactory: null, new DomainVerificationFailureRegistry());
        using var replica1 = new NotFoundEnumerationGuard(options, clock, metrics, NullLogger<NotFoundEnumerationGuard>.Instance, store);
        using var replica2 = new NotFoundEnumerationGuard(options, clock, metrics, NullLogger<NotFoundEnumerationGuard>.Instance, store);

        const string attacker = "203.0.113.0/24";

        // Split 404 misses across two replicas.
        Assert.Equal(NotFoundVerdict.WithinBudget, await replica1.RegisterAsync(attacker, TestContext.Current.CancellationToken));
        Assert.Equal(NotFoundVerdict.WithinBudget, await replica2.RegisterAsync(attacker, TestContext.Current.CancellationToken));
        // 3rd request drains distributed bucket on replica1 and writes Valkey shadow ban.
        Assert.Equal(NotFoundVerdict.Exhausted, await replica1.RegisterAsync(attacker, TestContext.Current.CancellationToken));

        // Replica2 immediately sees the distributed ban in Valkey.
        Assert.True(await replica2.IsShadowBannedAsync(attacker, TestContext.Current.CancellationToken));
        Assert.False(await replica2.IsShadowBannedAsync("198.51.100.0/24", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthAttemptLimiter_SharesBudgetAcrossInstancesAndFallsBackOnError()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, MockRedisState state) = MockRedis.Create();
        var store = new ValkeyRateLimitStore(clock, NullLogger<ValkeyRateLimitStore>.Instance, redis);
        IOptions<DleRateLimitOptions> options = Options.Create(new DleRateLimitOptions
        {
            AuthAttemptBurst = 2,
            AuthAttemptsPerMinute = 2,
        });

        using var control1 = new DleAuthAttemptLimiter(options, clock, store);
        using var control2 = new DleAuthAttemptLimiter(options, clock, store);

        Assert.True(await control1.TryAcquireAsync("198.51.100.0/24", "dle_shared", TestContext.Current.CancellationToken));
        Assert.True(await control2.TryAcquireAsync("198.51.100.0/24", "dle_shared", TestContext.Current.CancellationToken));
        Assert.False(await control1.TryAcquireAsync("198.51.100.0/24", "dle_shared", TestContext.Current.CancellationToken));

        // When Valkey errors, falls back cleanly to local limiter without rejecting everything.
        state.ExceptionToThrow = new TimeoutException("boom");
        Assert.True(await control1.TryAcquireAsync("198.51.100.0/24", "dle_shared", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AttributionRateLimiters_ShareClaimCodeFailuresAcrossInstancesAndFallbackOnError()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, MockRedisState state) = MockRedis.Create();
        var store = new ValkeyRateLimitStore(clock, NullLogger<ValkeyRateLimitStore>.Instance, redis);
        IOptions<AttributionRateLimitOptions> options = Options.Create(new AttributionRateLimitOptions
        {
            ClaimCodeFailurePermitLimit = 2,
            ClaimCodeFailureWindowSeconds = 60,
        });

        using var attr1 = new AttributionRateLimiters(options, store);
        using var attr2 = new AttributionRateLimiters(options, store);

        string callerKey = "tenantA|203.0.113.0/24";
        await attr1.RegisterClaimCodeFailureAsync(callerKey, TestContext.Current.CancellationToken);
        Assert.False(await attr2.IsClaimCodeGuessingBlockedAsync(callerKey, TestContext.Current.CancellationToken));

        await attr2.RegisterClaimCodeFailureAsync(callerKey, TestContext.Current.CancellationToken);
        Assert.True(await attr1.IsClaimCodeGuessingBlockedAsync(callerKey, TestContext.Current.CancellationToken));

        // Lease retry-after metadata stays intact on refused fixed window when distributed.
        using var attrResolve = new AttributionRateLimiters(
            Options.Create(new AttributionRateLimitOptions { ResolvePermitLimit = 1, ResolveWindowMinutes = 10 }),
            store);

        using RateLimitLease lease1 = await attrResolve.AcquireResolveAsync("inst-1", TestContext.Current.CancellationToken);
        Assert.True(lease1.IsAcquired);
        using RateLimitLease lease2 = await attrResolve.AcquireResolveAsync("inst-1", TestContext.Current.CancellationToken);
        Assert.False(lease2.IsAcquired);
        Assert.NotNull(AttributionRateLimiters.RetryAfter(lease2));

        // Fallback works seamlessly if Valkey fails.
        state.ExceptionToThrow = new TimeoutException("Valkey unreachable");
        using RateLimitLease fallbackLease = await attrResolve.AcquireResolveAsync("inst-2", TestContext.Current.CancellationToken);
        Assert.True(fallbackLease.IsAcquired);
    }

    public sealed class MockRedisState
    {
        private int _calls;

        public bool IsConnected { get; set; } = true;
        public int Calls => Volatile.Read(ref _calls);
        public Exception? ExceptionToThrow { get; set; }
        public ConcurrentDictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);

        public int NextCall() => Interlocked.Increment(ref _calls);
    }

    private static class MockRedis
    {
        public static (IConnectionMultiplexer Multiplexer, MockRedisState State) Create()
        {
            var state = new MockRedisState();
            IDatabase db = DispatchProxy.Create<IDatabase, MockDatabaseProxy>();
            ((MockDatabaseProxy)(object)db).State = state;

            IConnectionMultiplexer mux = DispatchProxy.Create<IConnectionMultiplexer, MockMultiplexerProxy>();
            var muxProxy = (MockMultiplexerProxy)(object)mux;
            muxProxy.State = state;
            muxProxy.Database = db;

            return (mux, state);
        }
    }

    public class MockMultiplexerProxy : DispatchProxy
    {
        public MockRedisState State { get; set; } = null!;
        public IDatabase Database { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_IsConnected" => State.IsConnected,
                "GetDatabase" => Database,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
    }

    public class MockDatabaseProxy : DispatchProxy
    {
        public MockRedisState State { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            State.NextCall();

            if (targetMethod?.Name == nameof(IDatabase.ScriptEvaluateAsync) && args is { Length: >= 3 })
            {
                if (State.ExceptionToThrow is not null)
                {
                    return Task.FromException<RedisResult>(State.ExceptionToThrow);
                }

                string script = (string)args[0]!;
                RedisKey[] keys = (RedisKey[])args[1]!;
                RedisValue[] values = (RedisValue[])args[2]!;
                string key = keys[0].ToString();

                // Both FixedWindow/TokenBucket track remaining vs limit simply here for deterministic unit assertions:
                if (script.Contains("HMGET", StringComparison.Ordinal))
                {
                    // TokenBucket: ARGV[1]=burst, ARGV[4]=cost
                    long burst = (long)values[0];
                    long cost = (long)values[3];
                    long currentTokens = State.Counters.GetOrAdd(key, burst);
                    if (currentTokens < cost)
                    {
                        return Task.FromResult(DecisionResult(0, currentTokens, 1000));
                    }

                    long next = State.Counters.AddOrUpdate(key, burst - cost, (_, old) => old - cost);
                    if (next < 0)
                    {
                        return Task.FromResult(DecisionResult(0, 0, 1000));
                    }

                    return Task.FromResult(DecisionResult(1, next, 0));
                }

                // FixedWindow & SlidingWindow: ARGV[1]=limit, ARGV[last]=cost
                long limit = (long)values[0];
                long windowCost = (long)values[^1];
                long current = State.Counters.GetOrAdd(key, 0L);
                if (current + windowCost > limit)
                {
                    return Task.FromResult(DecisionResult(0, Math.Max(0, limit - current), 5000));
                }

                long updated = State.Counters.AddOrUpdate(key, windowCost, (_, old) => old + windowCost);
                State.Strings[key] = updated.ToString(CultureInfo.InvariantCulture);
                return Task.FromResult(DecisionResult(1, Math.Max(0, limit - updated), 5000));
            }

            if (targetMethod?.Name == nameof(IDatabase.StringGetAsync) && args is { Length: >= 1 })
            {
                if (State.ExceptionToThrow is not null)
                {
                    return Task.FromException<RedisValue>(State.ExceptionToThrow);
                }

                string key = ((RedisKey)args[0]!).ToString();
                if (State.Strings.TryGetValue(key, out string? v))
                {
                    return Task.FromResult((RedisValue)v);
                }

                return Task.FromResult(RedisValue.Null);
            }

            if (targetMethod?.Name == nameof(IDatabase.StringSetAsync) && args is { Length: >= 2 })
            {
                if (State.ExceptionToThrow is not null)
                {
                    return Task.FromException<bool>(State.ExceptionToThrow);
                }

                string key = ((RedisKey)args[0]!).ToString();
                State.Strings[key] = ((RedisValue)args[1]!).ToString();
                return Task.FromResult(true);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        private static RedisResult DecisionResult(long allowed, long remaining, long retryMs) =>
            RedisResult.Create([
                RedisResult.Create((RedisValue)allowed),
                RedisResult.Create((RedisValue)remaining),
                RedisResult.Create((RedisValue)retryMs),
            ]);
    }
}
