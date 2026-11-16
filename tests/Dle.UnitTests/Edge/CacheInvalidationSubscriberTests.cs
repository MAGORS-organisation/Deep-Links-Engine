using Dle.Edge.Resolution;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// The cache invalidation subscriber must not fault when the host stops while a subscription to an
/// unreachable Valkey is still in flight. Valkey being down only costs latency; it must not
/// turn an ordinary shutdown into an error.
/// </summary>
public sealed class CacheInvalidationSubscriberTests
{
    [Fact]
    public async Task Stop_WhileSubscribingToAnUnreachableValkey_DoesNotFault()
    {
        ConfigurationOptions options = new()
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 100,
            // The backlog waits SyncTimeout for a connection before failing the subscription.
            SyncTimeout = 1000,
            AsyncTimeout = 1000,
            ConnectRetry = 0,
        };
        options.EndPoints.Add("127.0.0.1", 1);

        await using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(options);
        await using ServiceProvider services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        using CacheInvalidationSubscriber subscriber = new(
            services.GetRequiredService<HybridCache>(),
            NullLogger<CacheInvalidationSubscriber>.Instance,
            redis);

        // ExecuteAsync runs in the background, so give it time to reach the subscription, then stop
        // while the subscription is still waiting (SyncTimeout) for a connection that never comes.
        // It fails only after the stopping token has been cancelled.
        await subscriber.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        await subscriber.StopAsync(TestContext.Current.CancellationToken);

        Exception? fault = await Record.ExceptionAsync(() => subscriber.ExecuteTask!);
        Assert.Null(fault);
    }
}
