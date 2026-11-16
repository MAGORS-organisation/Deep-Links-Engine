using System.Threading.RateLimiting;

using Dle.Control.Configuration;
using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The rate limits of the SDK endpoints, keyed by installation and by caller address.
/// </summary>
/// <remarks>
/// <para>
/// These are endpoint-level limiters rather than <c>Microsoft.AspNetCore.RateLimiting</c> policies
/// for one concrete reason: both SDK limits are keyed on <c>install_id</c>, and <c>install_id</c>
/// arrives in the JSON body. The rate limiting middleware runs long before the body is read, so a
/// middleware policy would have to buffer and parse every request just to find its partition key —
/// which is exactly the work the limit exists to avoid. Acquiring the lease after the body is
/// deserialized costs one parse of a bounded payload and gets the correct partition.
/// </para>
/// <para>
/// Resolve is a fixed window of five per hour per installation,
/// because a correct SDK calls it once in the lifetime of an installation and five leaves room for
/// four retries. Events is a token bucket of sixty a minute with a burst of a hundred and twenty,
/// which lets an application that was offline flush its queue in one go and then settle. Wrong
/// claim codes are limited per tenant and network prefix rather than per installation, because
/// <c>install_id</c> is chosen by the caller and a guesser simply sends a new one each time.
/// </para>
/// <para>
/// Every limiter has <c>QueueLimit = 0</c>. A queued SDK request is a request the device is waiting
/// on; refusing immediately with <c>Retry-After</c> is both cheaper and more honest than holding the
/// connection open.
/// </para>
/// </remarks>
public sealed class AttributionRateLimiters : IDisposable
{
    private readonly PartitionedRateLimiter<string> _resolve;
    private readonly PartitionedRateLimiter<string> _events;
    private readonly PartitionedRateLimiter<string> _claimCodeFailures;
    private readonly int _claimCodeFailurePermitLimit;
    private readonly TimeSpan _claimCodeFailureWindow;
    private readonly PartitionedRateLimiter<string> _claimCodeIssue;
    private readonly PartitionedRateLimiter<string> _linkResolve;
    private readonly PartitionedRateLimiter<string> _linkResolveAnonymous;
    private readonly PartitionedRateLimiter<string> _loginHint;
    private readonly ValkeyRateLimitStore? _distributedStore;
    private bool _disposed;

    /// <summary>
    /// Creates the limiters.
    /// </summary>
    /// <param name="options">The configured limits.</param>
    /// <param name="distributedStore">Optional Valkey store for sharing attribution limits across replicas.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public AttributionRateLimiters(
        IOptions<AttributionRateLimitOptions> options,
        ValkeyRateLimitStore? distributedStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        AttributionRateLimitOptions limits = options.Value;
        _distributedStore = distributedStore;
        _claimCodeFailurePermitLimit = limits.ClaimCodeFailurePermitLimit;

        TimeSpan resolveWindow = TimeSpan.FromMinutes(limits.ResolveWindowMinutes);
        _resolve = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.FixedWindow(
                    "attr:resolve:" + partition,
                    limits.ResolvePermitLimit,
                    resolveWindow,
                    distributedStore)));

        TimeSpan eventsPeriod = TimeSpan.FromSeconds(limits.EventsPeriodSeconds);
        _events = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.TokenBucket(
                    "attr:events:" + partition,
                    limits.EventsBurstLimit,
                    limits.EventsTokensPerPeriod,
                    eventsPeriod,
                    distributedStore)));

        _claimCodeFailureWindow = TimeSpan.FromSeconds(limits.ClaimCodeFailureWindowSeconds);
        _claimCodeFailures = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.FixedWindow(
                    "attr:ccf:" + partition,
                    limits.ClaimCodeFailurePermitLimit,
                    _claimCodeFailureWindow,
                    distributedStore)));

        TimeSpan claimCodeIssueWindow = TimeSpan.FromSeconds(limits.ClaimCodeIssueWindowSeconds);
        _claimCodeIssue = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.FixedWindow(
                    "attr:cci:" + partition,
                    limits.ClaimCodeIssuePermitLimit,
                    claimCodeIssueWindow,
                    distributedStore)));

        TimeSpan linkResolvePeriod = TimeSpan.FromSeconds(limits.LinkResolvePeriodSeconds);
        _linkResolve = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.TokenBucket(
                    "attr:lr:" + partition,
                    limits.LinkResolveBurstLimit,
                    limits.LinkResolveTokensPerPeriod,
                    linkResolvePeriod,
                    distributedStore)));

        _linkResolveAnonymous = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.FixedWindow(
                    "attr:lra:" + partition,
                    limits.LinkResolveAnonymousPerMinute,
                    TimeSpan.FromMinutes(1),
                    distributedStore)));

        TimeSpan loginHintWindow = TimeSpan.FromSeconds(limits.LoginHintWindowSeconds);
        _loginHint = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(
                key,
                partition => ValkeyBackedRateLimiter.FixedWindow(
                    "attr:lhint:" + partition,
                    limits.LoginHintPermitLimit,
                    loginHintWindow,
                    distributedStore)));
    }

    /// <summary>Takes one permit from the direct-open limit of an installation.</summary>
    /// <param name="installId">The installation identifier from the request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireLinkResolveAsync(string installId, CancellationToken cancellationToken) =>
        _linkResolve.AcquireAsync(installId, 1, cancellationToken);

    /// <summary>
    /// Takes one permit from the direct-open limit of a caller network, for a request without an
    /// installation identifier.
    /// </summary>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireAnonymousLinkResolveAsync(string callerKey, CancellationToken cancellationToken) =>
        _linkResolveAnonymous.AcquireAsync(callerKey, 1, cancellationToken);

    /// <summary>Takes one permit from the resolve limit of an installation.</summary>
    /// <param name="installId">The installation identifier from the request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease. The caller must dispose it and must check
    /// <see cref="RateLimitLease.IsAcquired"/>.</returns>
    public ValueTask<RateLimitLease> AcquireResolveAsync(string installId, CancellationToken cancellationToken) =>
        _resolve.AcquireAsync(installId, 1, cancellationToken);

    /// <summary>Takes one permit per event from the event bucket of an installation.</summary>
    /// <param name="installId">The installation identifier from the request body.</param>
    /// <param name="permits">Number of events in the batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    /// <remarks>
    /// The batch costs one permit per event, not one per request. A limit of sixty requests a
    /// minute with a hundred events each would be six thousand events a minute, which is not what the
    /// limit intends.
    /// </remarks>
    public ValueTask<RateLimitLease> AcquireEventsAsync(string installId, int permits, CancellationToken cancellationToken) =>
        _events.AcquireAsync(installId, Math.Max(permits, 1), cancellationToken);

    /// <summary>How long a caller that ran out of wrong claim codes should wait.</summary>
    public TimeSpan ClaimCodeFailureWindow => _claimCodeFailureWindow;

    /// <summary>
    /// Whether a caller has presented so many wrong claim codes that further attempts are refused.
    /// </summary>
    /// <remarks>
    /// Read without taking a permit: only a wrong code costs one
    /// (<see cref="RegisterClaimCodeFailure"/>), so a correct code is never refused on account of the
    /// caller's own earlier successes.
    /// </remarks>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    /// <returns><see langword="true"/> when the budget of the current window is spent.</returns>
    public bool IsClaimCodeGuessingBlocked(string callerKey) =>
        _claimCodeFailures.GetStatistics(callerKey) is { CurrentAvailablePermits: <= 0 };

    /// <summary>
    /// Whether a caller has presented so many wrong claim codes that further attempts are refused
    /// (distributed via Valkey when available, with local fallback).
    /// </summary>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the budget of the current window is spent.</returns>
    public async ValueTask<bool> IsClaimCodeGuessingBlockedAsync(
        string callerKey,
        CancellationToken cancellationToken = default)
    {
        if (_distributedStore is { IsAvailable: true })
        {
            long? used = await _distributedStore.GetCounterAsync("attr:ccf:" + callerKey, cancellationToken)
                .ConfigureAwait(false);

            if (used.HasValue)
            {
                return used.Value >= _claimCodeFailurePermitLimit;
            }
        }

        return IsClaimCodeGuessingBlocked(callerKey);
    }

    /// <summary>Charges one wrong claim code to a caller (distributed via Valkey when available).</summary>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask RegisterClaimCodeFailureAsync(
        string callerKey,
        CancellationToken cancellationToken = default)
    {
        using RateLimitLease lease = await _claimCodeFailures.AcquireAsync(callerKey, 1, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Charges one wrong claim code to a caller.</summary>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    public void RegisterClaimCodeFailure(string callerKey)
    {
        using RateLimitLease lease = _claimCodeFailures.AttemptAcquire(callerKey);
    }

    /// <summary>Takes one permit from the claim code issuance limit of a caller.</summary>
    /// <param name="callerKey">Coarse caller identity, normally the tenant that asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireClaimCodeIssueAsync(string callerKey, CancellationToken cancellationToken) =>
        _claimCodeIssue.AcquireAsync(callerKey, 1, cancellationToken);

    /// <summary>Takes one permit from the S2 login-hint limit of a tenant and caller network.</summary>
    /// <param name="callerKey">Tenant and truncated network prefix of the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireLoginHintAsync(string callerKey, CancellationToken cancellationToken) =>
        _loginHint.AcquireAsync(callerKey, 1, cancellationToken);

    /// <summary>Reads the wait suggested by a refused lease.</summary>
    /// <param name="lease">The refused lease.</param>
    /// <returns>The suggested wait, or <see langword="null"/> when the limiter did not offer one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lease"/> is <see langword="null"/>.</exception>
    public static TimeSpan? RetryAfter(RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter) ? retryAfter : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _resolve.Dispose();
        _events.Dispose();
        _claimCodeFailures.Dispose();
        _claimCodeIssue.Dispose();
        _linkResolve.Dispose();
        _linkResolveAnonymous.Dispose();
        _loginHint.Dispose();
    }
}
