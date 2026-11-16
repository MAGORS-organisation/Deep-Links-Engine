using Dle.Control.Configuration;
using Dle.Control.Identity;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Hardening checks for <see cref="DleCredentialCache"/> and
/// <see cref="CredentialCacheInvalidationSubscriber"/>: expiry capping, immediate
/// key/tenant/app revocation, generation fencing against verification races, and pub/sub parsing.
/// </summary>
public sealed class CredentialCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Set_CredentialAlreadyExpired_IsNotCached()
    {
        var clock = new ControlTestClock(Now);
        using DleCredentialCache cache = CreateCache(clock);

        cache.Set("api", "secret-key", "cached", Guid.NewGuid(), Guid.NewGuid(), expiresAt: Now.AddSeconds(-1));

        Assert.False(cache.TryGet("api", "secret-key", out string? _));
    }

    [Fact]
    public async Task InvalidateKeyTenantAndApp_RemoveMatchingEntriesImmediately()
    {
        var clock = new ControlTestClock(Now);
        using DleCredentialCache cache = CreateCache(clock);

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Guid keyA1 = Guid.NewGuid();
        Guid keyA2 = Guid.NewGuid();
        Guid appB1 = Guid.NewGuid();
        Guid keyB1 = Guid.NewGuid();

        cache.Set("api", "k-a1", "a1", keyA1, tenantA, expiresAt: Now.AddHours(1));
        cache.Set("api", "k-a2", "a2", keyA2, tenantA, expiresAt: Now.AddHours(1));
        cache.Set("sdk", "k-b1", "b1", keyB1, tenantB, expiresAt: Now.AddHours(1), appId: appB1);

        await cache.InvalidateKeyAsync(keyA1, TestContext.Current.CancellationToken);
        Assert.False(cache.TryGet("api", "k-a1", out string? _));
        Assert.True(cache.TryGet("api", "k-a2", out string? _));
        Assert.True(cache.TryGet("sdk", "k-b1", out string? _));

        await cache.InvalidateTenantAsync(tenantA, TestContext.Current.CancellationToken);
        Assert.False(cache.TryGet("api", "k-a2", out string? _));
        Assert.True(cache.TryGet("sdk", "k-b1", out string? _));

        await cache.InvalidateAppAsync(appB1, TestContext.Current.CancellationToken);
        Assert.False(cache.TryGet("sdk", "k-b1", out string? _));
    }

    [Fact]
    public void Set_WithStaleSnapshotGenerationAfterInvalidation_DoesNotRepopulate()
    {
        var clock = new ControlTestClock(Now);
        using DleCredentialCache cache = CreateCache(clock);

        Guid keyId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();
        long captured = cache.CaptureGeneration();

        cache.InvalidateKey(keyId);
        cache.Set("api", "revoked-secret", "identity", keyId, tenantId, snapshotGeneration: captured);

        Assert.False(cache.TryGet("api", "revoked-secret", out string? _));
    }

    [Fact]
    public void SubscriberApplyMessage_InvalidatesTargetAndIgnoresMalformedPayloads()
    {
        var clock = new ControlTestClock(Now);
        using DleCredentialCache cache = CreateCache(clock);
        using var subscriber = new CredentialCacheInvalidationSubscriber(
            cache,
            NullLogger<CredentialCacheInvalidationSubscriber>.Instance);

        Guid keyId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        cache.Set("api", "k1", "v1", keyId, tenantId);
        cache.Set("sdk", "k2", "v2", Guid.NewGuid(), tenantId, appId: appId);

        subscriber.ApplyMessage("invalid");
        subscriber.ApplyMessage("key:not-a-guid");
        Assert.True(cache.TryGet("api", "k1", out string? _));

        subscriber.ApplyMessage("key:" + keyId.ToString("D"));
        Assert.False(cache.TryGet("api", "k1", out string? _));
        Assert.True(cache.TryGet("sdk", "k2", out string? _));

        subscriber.ApplyMessage("app:" + appId.ToString("D"));
        Assert.False(cache.TryGet("sdk", "k2", out string? _));
    }

    private static DleCredentialCache CreateCache(TimeProvider clock) => new(
        Options.Create(new DleIdentityOptions { CredentialCacheSeconds = 60 }),
        clock);
}
