using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Dle.Control.Configuration;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Dle.Control.Identity;

/// <summary>
/// Remembers, for a short window, that a particular presented key verified against a particular key
/// row, with bounded expiry and immediate local plus multi-replica revocation invalidation.
/// </summary>
/// <remarks>
/// <para>
/// Argon2id costs roughly nineteen mebibytes and a couple of passes per verification. That is
/// deliberate - it is what makes a stolen <c>api_keys</c> table useless - but paying it on every
/// request would put tens of milliseconds and a large allocation on every control-plane call, and
/// would hand any authenticated client a memory-exhaustion lever.
/// </para>
/// <para>
/// What is cached is a decision, not a credential. The entry key is a keyed HMAC-SHA-256 of the
/// presented value under an ephemeral process pepper, so the cache cannot be read back into a
/// working key even from process memory; the value is the already-public identity the verification
/// established. Entries are capped by the credential's <c>expires_at</c> as well as the cache
/// window, indexed by key, tenant, and application identifiers for immediate revocation across all
/// replicas via Valkey Pub/Sub, and guarded by a monotonic generation counter against concurrent
/// verification-vs-revocation races.
/// </para>
/// </remarks>
public sealed class DleCredentialCache : IDisposable
{
    /// <summary>How many verified decisions the cache holds before evicting the least recent.</summary>
    private const long Capacity = 10_000;

    /// <summary>Pub/Sub channel carrying key, tenant, and app credential invalidation events.</summary>
    public const string InvalidationChannel = "dle:credential:invalidation";

    private readonly MemoryCache _cache;
    private readonly MemoryCache _keyInvalidations;
    private readonly MemoryCache _tenantInvalidations;
    private readonly MemoryCache _appInvalidations;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> _keyFingerprints = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> _tenantFingerprints = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> _appFingerprints = new();
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<DleCredentialCache>? _logger;
    private readonly byte[] _pepper = RandomNumberGenerator.GetBytes(32);
    private long _generation;
    private long _nextEntryId;
    private bool _disposed;

    /// <summary>Creates the cache.</summary>
    /// <param name="options">Identity options carrying the cache window.</param>
    /// <param name="timeProvider">Optional clock used when bounding entries against key expiry.</param>
    /// <param name="redis">Optional Valkey connection multiplexer for cross-replica invalidation.</param>
    /// <param name="logger">Optional logger.</param>
    /// <remarks>
    /// The cache is its own <see cref="MemoryCache"/> rather than the application-wide one, so that
    /// its bound is a property of this class and a flood of authentication attempts cannot evict
    /// anything else the process is caching.
    /// </remarks>
    public DleCredentialCache(
        IOptions<DleIdentityOptions> options,
        TimeProvider? timeProvider = null,
        IConnectionMultiplexer? redis = null,
        ILogger<DleCredentialCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _lifetime = TimeSpan.FromSeconds(options.Value.CredentialCacheSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _redis = redis;
        _logger = logger;
        _cache = CreateBoundedCache();
        _keyInvalidations = CreateBoundedCache();
        _tenantInvalidations = CreateBoundedCache();
        _appInvalidations = CreateBoundedCache();
    }

    /// <summary>Whether the cache is switched on at all.</summary>
    public bool IsEnabled => _lifetime > TimeSpan.Zero;

    /// <summary>
    /// Captures the current invalidation generation before reading credentials from the backing
    /// store so a concurrent revocation cannot be overwritten by an in-flight verification.
    /// </summary>
    /// <returns>The current invalidation generation token.</returns>
    public long CaptureGeneration() => Volatile.Read(ref _generation);

    /// <summary>Looks up a previously verified decision.</summary>
    /// <typeparam name="TValue">Type of the cached identity.</typeparam>
    /// <param name="scheme">Authentication scheme, so the two key kinds never share an entry.</param>
    /// <param name="presentedKey">The key as received. Never stored.</param>
    /// <param name="value">The cached identity, when present.</param>
    /// <returns><see langword="true"/> on a hit.</returns>
    public bool TryGet<TValue>(string scheme, string presentedKey, out TValue? value)
        where TValue : class
    {
        if (!IsEnabled || _disposed)
        {
            value = null;
            return false;
        }

        if (_cache.TryGetValue(Fingerprint(scheme, presentedKey), out object? cached)
            && cached is TValue typed)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Records a successful verification for an unbound credential decision.</summary>
    /// <typeparam name="TValue">Type of the identity to cache.</typeparam>
    /// <param name="scheme">Authentication scheme.</param>
    /// <param name="presentedKey">The key as received. Never stored.</param>
    /// <param name="value">The identity the verification established.</param>
    public void Set<TValue>(string scheme, string presentedKey, TValue value)
        where TValue : class
        => Set(scheme, presentedKey, value, Guid.Empty, Guid.Empty);

    /// <summary>
    /// Records a successful verification indexed by credential, tenant, and optional app identifier,
    /// bounded by <paramref name="expiresAt"/> and guarded against concurrent invalidation.
    /// </summary>
    /// <typeparam name="TValue">Type of the identity to cache.</typeparam>
    /// <param name="scheme">Authentication scheme.</param>
    /// <param name="presentedKey">The key as received. Never stored.</param>
    /// <param name="value">The identity the verification established.</param>
    /// <param name="keyId">Key row identifier for targeted invalidation.</param>
    /// <param name="tenantId">Tenant identifier for tenant-wide invalidation.</param>
    /// <param name="expiresAt">Optional credential expiry timestamp.</param>
    /// <param name="appId">Optional owning application identifier for SDK keys.</param>
    /// <param name="snapshotGeneration">
    /// Generation captured via <see cref="CaptureGeneration"/> prior to reading the store.
    /// </param>
    public void Set<TValue>(
        string scheme,
        string presentedKey,
        TValue value,
        Guid keyId,
        Guid tenantId,
        DateTimeOffset? expiresAt = null,
        Guid? appId = null,
        long? snapshotGeneration = null)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsEnabled || _disposed)
        {
            return;
        }

        if (snapshotGeneration is long captured && WasInvalidatedAfter(keyId, tenantId, appId, captured))
        {
            return;
        }

        TimeSpan effectiveLifetime = _lifetime;

        if (expiresAt is DateTimeOffset expiry)
        {
            TimeSpan remaining = expiry - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining < effectiveLifetime)
            {
                effectiveLifetime = remaining;
            }
        }

        string cacheKey = Fingerprint(scheme, presentedKey);
        long entryId = Interlocked.Increment(ref _nextEntryId);

        TrackFingerprint(_keyFingerprints, keyId, cacheKey, entryId);
        TrackFingerprint(_tenantFingerprints, tenantId, cacheKey, entryId);

        if (appId is Guid aid && aid != Guid.Empty)
        {
            TrackFingerprint(_appFingerprints, aid, cacheKey, entryId);
        }

        var entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = effectiveLifetime,
            Size = 1,
        };

        entryOptions.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            if (evictedKey is string evictedString)
            {
                UntrackFingerprint(_keyFingerprints, keyId, evictedString, entryId);
                UntrackFingerprint(_tenantFingerprints, tenantId, evictedString, entryId);

                if (appId is Guid evictedApp && evictedApp != Guid.Empty)
                {
                    UntrackFingerprint(_appFingerprints, evictedApp, evictedString, entryId);
                }
            }
        });

        _cache.Set(cacheKey, value, entryOptions);

        // Re-check after Set in case an invalidation ran concurrently with index registration.
        if (snapshotGeneration is long afterSet && WasInvalidatedAfter(keyId, tenantId, appId, afterSet))
        {
            _cache.Remove(cacheKey);
        }
    }

    /// <summary>Invalidates all cached entries for a specific API or SDK key locally.</summary>
    /// <param name="keyId">Revoked key identifier.</param>
    public void InvalidateKey(Guid keyId)
    {
        if (keyId == Guid.Empty || _disposed)
        {
            return;
        }

        RecordInvalidation(_keyInvalidations, keyId);
        RemoveTrackedFingerprints(_keyFingerprints, keyId);
    }

    /// <summary>
    /// Invalidates all cached entries for a specific key locally and broadcasts the invalidation
    /// across replicas via Valkey Pub/Sub when configured.
    /// </summary>
    /// <param name="keyId">Revoked key identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask InvalidateKeyAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        InvalidateKey(keyId);
        return PublishInvalidationAsync("key", keyId, cancellationToken);
    }

    /// <summary>Invalidates all cached credentials belonging to a tenant locally.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    public void InvalidateTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || _disposed)
        {
            return;
        }

        RecordInvalidation(_tenantInvalidations, tenantId);
        RemoveTrackedFingerprints(_tenantFingerprints, tenantId);
    }

    /// <summary>
    /// Invalidates all cached credentials for a tenant locally and broadcasts across replicas.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask InvalidateTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        InvalidateTenant(tenantId);
        return PublishInvalidationAsync("tenant", tenantId, cancellationToken);
    }

    /// <summary>Invalidates all cached SDK keys belonging to a deleted application locally.</summary>
    /// <param name="appId">Application identifier.</param>
    public void InvalidateApp(Guid appId)
    {
        if (appId == Guid.Empty || _disposed)
        {
            return;
        }

        RecordInvalidation(_appInvalidations, appId);
        RemoveTrackedFingerprints(_appFingerprints, appId);
    }

    /// <summary>
    /// Invalidates all cached SDK keys for a deleted application locally and across replicas.
    /// </summary>
    /// <param name="appId">Application identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask InvalidateAppAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        InvalidateApp(appId);
        return PublishInvalidationAsync("app", appId, cancellationToken);
    }

    private static MemoryCache CreateBoundedCache() =>
        new(Microsoft.Extensions.Options.Options.Create(
            new MemoryCacheOptions { SizeLimit = Capacity }));

    private void RecordInvalidation(MemoryCache tombstones, Guid id)
    {
        long gen = Interlocked.Increment(ref _generation);
        TimeSpan tombstoneTtl = (_lifetime > TimeSpan.Zero ? _lifetime : TimeSpan.FromMinutes(1))
            + TimeSpan.FromSeconds(10);

        tombstones.Set(
            id,
            gen,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = tombstoneTtl,
                Size = 1,
            });
    }

    private bool WasInvalidatedAfter(Guid keyId, Guid tenantId, Guid? appId, long snapshotGeneration) =>
        (keyId != Guid.Empty && WasTargetInvalidatedAfter(_keyInvalidations, keyId, snapshotGeneration))
        || (tenantId != Guid.Empty && WasTargetInvalidatedAfter(_tenantInvalidations, tenantId, snapshotGeneration))
        || (appId is Guid aid && aid != Guid.Empty && WasTargetInvalidatedAfter(_appInvalidations, aid, snapshotGeneration));

    private static bool WasTargetInvalidatedAfter(MemoryCache tombstones, Guid id, long snapshotGeneration) =>
        tombstones.TryGetValue(id, out object? raw) && raw is long gen && gen > snapshotGeneration;

    private static void TrackFingerprint(
        ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> index,
        Guid scopeId,
        string cacheKey,
        long entryId)
    {
        if (scopeId == Guid.Empty)
        {
            return;
        }

        ConcurrentDictionary<string, long> set = index.GetOrAdd(
            scopeId,
            static _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));

        set[cacheKey] = entryId;
    }

    private static void UntrackFingerprint(
        ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> index,
        Guid scopeId,
        string cacheKey,
        long entryId)
    {
        if (scopeId == Guid.Empty || !index.TryGetValue(scopeId, out ConcurrentDictionary<string, long>? set))
        {
            return;
        }

        set.TryRemove(new KeyValuePair<string, long>(cacheKey, entryId));

        if (set.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, long>>(scopeId, set));
        }
    }

    private void RemoveTrackedFingerprints(
        ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> index,
        Guid scopeId)
    {
        if (!index.TryRemove(scopeId, out ConcurrentDictionary<string, long>? set))
        {
            return;
        }

        foreach (string cacheKey in set.Keys)
        {
            _cache.Remove(cacheKey);
        }
    }

    private async ValueTask PublishInvalidationAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        if (_redis is null || !_redis.IsConnected || id == Guid.Empty)
        {
            return;
        }

        try
        {
            string payload = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{kind}:{id:D}");
            ISubscriber subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(InvalidationChannel), payload);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(
                ex,
                "Failed to publish credential cache invalidation for {Kind} {Id} to {Channel}.",
                kind,
                id,
                InvalidationChannel);
        }
    }

    /// <summary>
    /// Derives the cache key: HMAC-SHA-256 of the presented value under a per-process random pepper.
    /// </summary>
    /// <param name="scheme">Authentication scheme.</param>
    /// <param name="presentedKey">The key as received.</param>
    /// <returns>An opaque, non-reversible cache key.</returns>
    private string Fingerprint(string scheme, string presentedKey)
    {
        Span<byte> digest = stackalloc byte[32];
        byte[] material = Encoding.UTF8.GetBytes(string.Concat(scheme, " ", presentedKey));

        try
        {
            HMACSHA256.HashData(_pepper, material, digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_pepper);
        _cache.Dispose();
        _keyInvalidations.Dispose();
        _tenantInvalidations.Dispose();
        _appInvalidations.Dispose();
    }
}
