using System.Threading.RateLimiting;

using Dle.Persistence.Fast.Caching;

namespace Dle.Control.Configuration;

/// <summary>
/// Distributed Valkey <see cref="RateLimiter"/> that wraps any local
/// <see cref="System.Threading.RateLimiting"/> algorithm (sliding window, fixed window, or token
/// bucket) and allocates the local fallback lazily only when Valkey is unavailable.
/// </summary>
internal sealed class ValkeyBackedRateLimiter : RateLimiter
{
    private static readonly string[] RetryAfterMetadataNames = [MetadataName.RetryAfter.Name];

    private readonly ValkeyRateLimitStore? _store;
    private readonly Func<ValkeyRateLimitStore, int, CancellationToken, ValueTask<ValkeyRateLimitDecision?>> _acquireRemote;
    private readonly Func<RateLimiter> _fallbackFactory;
    private RateLimiter? _fallback;

    private ValkeyBackedRateLimiter(
        ValkeyRateLimitStore? store,
        Func<ValkeyRateLimitStore, int, CancellationToken, ValueTask<ValkeyRateLimitDecision?>> acquireRemote,
        Func<RateLimiter> fallbackFactory)
    {
        _store = store;
        _acquireRemote = acquireRemote;
        _fallbackFactory = fallbackFactory;
    }

    public static ValkeyBackedRateLimiter SlidingWindow(
        string valkeyKey,
        int permitLimit,
        TimeSpan window,
        int segments,
        ValkeyRateLimitStore? store) =>
        new(
            store,
            (s, cost, ct) => s.TryAcquireSlidingWindowAsync(valkeyKey, permitLimit, window, segments, cost, ct),
            () => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                SegmentsPerWindow = segments,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    public static ValkeyBackedRateLimiter FixedWindow(
        string valkeyKey,
        int permitLimit,
        TimeSpan window,
        ValkeyRateLimitStore? store) =>
        new(
            store,
            (s, cost, ct) => s.TryAcquireFixedWindowAsync(valkeyKey, permitLimit, window, cost, ct),
            () => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    public static ValkeyBackedRateLimiter TokenBucket(
        string valkeyKey,
        int burst,
        int tokensPerPeriod,
        TimeSpan period,
        ValkeyRateLimitStore? store) =>
        new(
            store,
            (s, cost, ct) => s.TryAcquireTokenBucketAsync(valkeyKey, burst, tokensPerPeriod, period, cost, ct),
            () => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod = period,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    public override TimeSpan? IdleDuration => _fallback?.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _fallback?.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        EnsureFallback().AttemptAcquire(permitCount);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_store is { IsAvailable: true })
        {
            ValkeyRateLimitDecision? decision = await _acquireRemote(_store, permitCount, cancellationToken)
                .ConfigureAwait(false);

            if (decision is { } result)
            {
                return new DistributedLease(result.IsAcquired, result.RetryAfter);
            }
        }

        return await EnsureFallback().AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fallback?.Dispose();
            _fallback = null;
        }

        base.Dispose(disposing);
    }

    private RateLimiter EnsureFallback()
    {
        RateLimiter? existing = Volatile.Read(ref _fallback);
        if (existing is not null)
        {
            return existing;
        }

        RateLimiter created = _fallbackFactory();
        RateLimiter? prior = Interlocked.CompareExchange(ref _fallback, created, null);
        if (prior is not null)
        {
            created.Dispose();
            return prior;
        }

        return created;
    }

    private sealed class DistributedLease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired { get; } = isAcquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter.HasValue ? RetryAfterMetadataNames : [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter.HasValue && string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
