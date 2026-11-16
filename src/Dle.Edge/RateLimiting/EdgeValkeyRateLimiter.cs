using System.Threading.RateLimiting;

using Dle.Persistence.Fast.Caching;

namespace Dle.Edge.RateLimiting;

/// <summary>
/// Valkey-backed sliding-window <see cref="RateLimiter"/> that falls back transparently to a lazy
/// in-process <see cref="SlidingWindowRateLimiter"/> when Valkey is unconfigured or unavailable.
/// </summary>
internal sealed class EdgeValkeyRateLimiter : RateLimiter
{
    private static readonly string[] RetryAfterMetadataNames = [MetadataName.RetryAfter.Name];

    private readonly string _valkeyKey;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly int _segments;
    private readonly ValkeyRateLimitStore? _store;
    private SlidingWindowRateLimiter? _fallback;

    public EdgeValkeyRateLimiter(
        string valkeyKey,
        int permitLimit,
        TimeSpan window,
        int segments,
        ValkeyRateLimitStore? store)
    {
        _valkeyKey = valkeyKey;
        _permitLimit = permitLimit;
        _window = window;
        _segments = segments;
        _store = store;
    }

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
            ValkeyRateLimitDecision? decision = await _store.TryAcquireSlidingWindowAsync(
                _valkeyKey,
                _permitLimit,
                _window,
                _segments,
                permitCount,
                cancellationToken).ConfigureAwait(false);

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

    private SlidingWindowRateLimiter EnsureFallback()
    {
        SlidingWindowRateLimiter? existing = Volatile.Read(ref _fallback);
        if (existing is not null)
        {
            return existing;
        }

        var created = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = _permitLimit,
            Window = _window,
            SegmentsPerWindow = _segments,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

        SlidingWindowRateLimiter? prior = Interlocked.CompareExchange(ref _fallback, created, null);
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
