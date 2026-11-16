using System.Collections.Concurrent;
using System.Reflection;

using Dle.Crypto;
using Dle.Domain.Crypto;
using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// Shared replay protection over Valkey (<see cref="ValkeyTokenReplayGuard"/>): atomic consume,
/// expiry guard, and fail-closed refusal when Valkey is disconnected or raises an error (L-6).
/// </summary>
public sealed class ValkeyTokenReplayGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryConsumeAsync_ExpiredToken_RefusesWithoutTouchingValkey()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, RedisState state) = FakeRedis.Create();
        var guard = new ValkeyTokenReplayGuard(redis, clock, NullLogger<ValkeyTokenReplayGuard>.Instance);

        Assert.False(await guard.TryConsumeAsync("expired-jti", Now, TestContext.Current.CancellationToken));
        Assert.False(await guard.TryConsumeAsync("expired-jti", Now.AddSeconds(-5), TestContext.Current.CancellationToken));
        Assert.Equal(0, state.Calls);
    }

    [Fact]
    public async Task TryConsumeAsync_FirstUseSucceeds_AndReplayIsRefused()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, RedisState state) = FakeRedis.Create();
        var guard = new ValkeyTokenReplayGuard(redis, clock, NullLogger<ValkeyTokenReplayGuard>.Instance);

        Assert.True(await guard.TryConsumeAsync("jti-abc", Now.AddMinutes(5), TestContext.Current.CancellationToken));
        Assert.False(await guard.TryConsumeAsync("jti-abc", Now.AddMinutes(5), TestContext.Current.CancellationToken));
        Assert.True(state.Keys.ContainsKey("dle:replay:jti:jti-abc"));
    }

    [Fact]
    public async Task TryConsumeAsync_DisconnectedOrFailingValkey_FailsClosed()
    {
        var clock = new CryptoTestClock(Now);
        (IConnectionMultiplexer redis, RedisState state) = FakeRedis.Create();
        var guard = new ValkeyTokenReplayGuard(redis, clock, NullLogger<ValkeyTokenReplayGuard>.Instance);

        state.IsConnected = false;
        Assert.False(await guard.TryConsumeAsync("jti-down", Now.AddMinutes(5), TestContext.Current.CancellationToken));

        state.IsConnected = true;
        state.ExceptionToThrow = new TimeoutException("Valkey timed out.");
        Assert.False(await guard.TryConsumeAsync("jti-timeout", Now.AddMinutes(5), TestContext.Current.CancellationToken));

        state.ExceptionToThrow = new RedisServerException("READONLY You can't write against a read only replica.");
        Assert.False(await guard.TryConsumeAsync("jti-redis-err", Now.AddMinutes(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddDleValkeyTokenReplayGuard_ReplacesEarlierInMemoryRegistration()
    {
        (IConnectionMultiplexer redis, _) = FakeRedis.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<ITokenReplayGuard, InMemoryTokenReplayGuard>();
        services.AddSingleton(redis);

        services.AddDleValkeyTokenReplayGuard();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<ValkeyTokenReplayGuard>(provider.GetRequiredService<ITokenReplayGuard>());
    }

    public sealed class RedisState
    {
        private int _calls;

        public bool IsConnected { get; set; } = true;
        public int Calls => Volatile.Read(ref _calls);
        public Exception? ExceptionToThrow { get; set; }
        public ConcurrentDictionary<string, TimeSpan?> Keys { get; } = new(StringComparer.Ordinal);

        public void IncrementCalls() => Interlocked.Increment(ref _calls);
    }

    private static class FakeRedis
    {
        public static (IConnectionMultiplexer Multiplexer, RedisState State) Create()
        {
            var state = new RedisState();
            IDatabase db = DispatchProxy.Create<IDatabase, DatabaseProxy>();
            ((DatabaseProxy)(object)db).State = state;

            IConnectionMultiplexer mux = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
            var muxProxy = (MultiplexerProxy)(object)mux;
            muxProxy.State = state;
            muxProxy.Database = db;

            return (mux, state);
        }
    }

    public class MultiplexerProxy : DispatchProxy
    {
        public RedisState State { get; set; } = null!;
        public IDatabase Database { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_IsConnected" => State.IsConnected,
                "GetDatabase" => Database,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }
    }

    public class DatabaseProxy : DispatchProxy
    {
        public RedisState State { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabase.StringSetAsync) && args is { Length: >= 4 })
            {
                State.IncrementCalls();
                if (State.ExceptionToThrow is not null)
                {
                    return Task.FromException<bool>(State.ExceptionToThrow);
                }

                string key = ((RedisKey)args[0]!).ToString();
                TimeSpan? expiry = args[2] as TimeSpan?;
                bool inserted = State.Keys.TryAdd(key, expiry);
                return Task.FromResult(inserted);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }
}
