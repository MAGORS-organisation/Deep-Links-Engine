using System.Collections.Concurrent;
using System.Threading.RateLimiting;

using Dle.Edge.Telemetry;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Edge.RateLimiting;

/// <summary>
/// The anti-enumeration budget of §E.9: a token bucket over resolve requests that ended in 404, and
/// a shadow ban for a network prefix that drains it (T-07, TC-108).
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important limit in the table, and the reason it lives here rather than in
/// the rate limiting middleware is that it is a limit on a <em>response</em>. Middleware decides
/// before the handler runs and therefore cannot know whether a request will find a link; only the
/// resolve pipeline can, which is why it calls <see cref="Register"/> at the point it has decided to
/// answer 404.
/// </para>
/// <para>
/// It is a separate partition from the sliding window over successful resolves, and that separation
/// is the whole point (§E.9, T-07). Sharing a counter would mean a campaign going viral — a burst of
/// <em>successes</em> — exhausts the budget and switches the enumeration defence off at the moment
/// the service is most visible and most worth scanning.
/// </para>
/// <para>
/// The ban is a shadow ban rather than a block. A banned prefix keeps receiving the same 404 body as
/// any other miss, produced without a cache lookup or a database query, so the scan looks exactly as
/// it did before while costing the service nothing. Answering 429 instead would tell the scanner it
/// had been detected and would hand it a reliable oracle for "this address range is being watched".
/// </para>
/// </remarks>
public sealed partial class NotFoundEnumerationGuard : IDisposable
{
    private readonly PartitionedRateLimiter<string> _budget;
    private readonly ConcurrentDictionary<string, long> _bans = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly EdgeMetrics _metrics;
    private readonly ILogger<NotFoundEnumerationGuard> _logger;
    private readonly int _capacity;
    private readonly int _shadowBanMinutes;
    private bool _disposed;

    /// <summary>Creates the guard.</summary>
    /// <param name="options">Edge rate limit options; supplies the §E.9 values.</param>
    /// <param name="timeProvider">Clock driving ban expiry (SHARED-KERNEL §17.2).</param>
    /// <param name="metrics">Edge instruments; counts requests answered from the ban list.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public NotFoundEnumerationGuard(
        IOptions<EdgeRateLimitOptions> options,
        TimeProvider timeProvider,
        EdgeMetrics metrics,
        ILogger<NotFoundEnumerationGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        NotFoundRateLimitOptions notFound = options.Value.NotFound;

        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
        _capacity = notFound.TrackedPrefixCapacity;
        _shadowBanMinutes = notFound.ShadowBanMinutes;
        ShadowBanWindow = TimeSpan.FromMinutes(notFound.ShadowBanMinutes);

        int tokensPerPeriod = notFound.TokensPerPeriod;
        int burst = Math.Max(notFound.Burst, tokensPerPeriod);
        var period = TimeSpan.FromSeconds(notFound.ReplenishmentPeriodSeconds);

        _budget = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod = period,

                // No queue. A caller that is out of tokens is told so immediately; making it wait
                // would hold a request open on the resolve path, which is the one place §17.8 rules
                // out blocking.
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));
    }

    /// <summary>How long a prefix stays shadow banned once it has drained its bucket.</summary>
    public TimeSpan ShadowBanWindow { get; }

    /// <summary>
    /// Whether a network prefix is currently shadow banned and must be answered without touching the
    /// cache or the database.
    /// </summary>
    /// <param name="networkKey">The client's network prefix.</param>
    /// <returns><see langword="true"/> when the prefix is banned.</returns>
    public bool IsShadowBanned(string networkKey)
    {
        if (string.IsNullOrEmpty(networkKey) || !_bans.TryGetValue(networkKey, out long expiresAt))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow().UtcTicks < expiresAt)
        {
            _metrics.RecordShadowBanned();
            return true;
        }

        // Expired. Removing it here rather than on a sweep keeps the common path allocation free and
        // means a prefix that stops scanning stops being tracked without any bookkeeping at all.
        _ = _bans.TryRemove(new KeyValuePair<string, long>(networkKey, expiresAt));
        return false;
    }

    /// <summary>
    /// Charges one 404 to a network prefix and reports whether the prefix still has budget.
    /// </summary>
    /// <param name="networkKey">The client's network prefix.</param>
    /// <returns>
    /// <see cref="NotFoundVerdict.WithinBudget"/> while the prefix has tokens, and
    /// <see cref="NotFoundVerdict.Exhausted"/> for the request that drains the bucket — which also
    /// arms the shadow ban.
    /// </returns>
    public NotFoundVerdict Register(string networkKey)
    {
        if (_disposed || string.IsNullOrEmpty(networkKey))
        {
            return NotFoundVerdict.WithinBudget;
        }

        using RateLimitLease lease = _budget.AttemptAcquire(networkKey);

        if (lease.IsAcquired)
        {
            return NotFoundVerdict.WithinBudget;
        }

        Ban(networkKey);
        return NotFoundVerdict.Exhausted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _budget.Dispose();
        _bans.Clear();
    }

    /// <summary>Puts a prefix on the ban list, keeping the list bounded.</summary>
    /// <remarks>
    /// The list is capacity bounded because its key space is chosen by the attacker: a scan from a
    /// large IPv6 allocation walks through many prefixes, and an unbounded dictionary would make the
    /// defence into the memory exhaustion primitive it was installed to prevent. Over capacity, expired
    /// entries are dropped first and the whole list only as a last resort — losing bans is a degraded
    /// defence, and a process that runs out of memory is no defence at all.
    /// </remarks>
    private void Ban(string networkKey)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_bans.Count >= _capacity)
        {
            Prune(now);
        }

        long expiresAt = now.Add(ShadowBanWindow).UtcTicks;

        if (_bans.TryAdd(networkKey, expiresAt))
        {
            EdgeLog.ShadowBanned(_logger, networkKey, _shadowBanMinutes);
        }
        else
        {
            _bans[networkKey] = expiresAt;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        long threshold = now.UtcTicks;

        foreach (KeyValuePair<string, long> entry in _bans)
        {
            if (entry.Value <= threshold)
            {
                _ = _bans.TryRemove(entry);
            }
        }

        if (_bans.Count >= _capacity)
        {
            BanListOverflowed(_logger, _bans.Count);
            _bans.Clear();
        }
    }

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Warning,
        Message = "The enumeration shadow ban list reached its capacity of {Count} network prefixes and was cleared; a distributed scan is in progress.")]
    private static partial void BanListOverflowed(ILogger logger, int count);
}

/// <summary>Outcome of charging one 404 to a network prefix.</summary>
public enum NotFoundVerdict
{
    /// <summary>The prefix still has tokens; answer the 404 normally.</summary>
    WithinBudget = 0,

    /// <summary>The prefix drained its bucket; it is now shadow banned and gets 429 once.</summary>
    Exhausted = 1,
}
