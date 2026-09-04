using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The rate limits of the SDK endpoints, keyed by installation and by caller address (§E.9).
/// </summary>
/// <remarks>
/// <para>
/// These are endpoint-level limiters rather than <c>Microsoft.AspNetCore.RateLimiting</c> policies
/// for one concrete reason: §E.9 keys both SDK limits on <c>install_id</c>, and <c>install_id</c>
/// arrives in the JSON body. The rate limiting middleware runs long before the body is read, so a
/// middleware policy would have to buffer and parse every request just to find its partition key —
/// which is exactly the work the limit exists to avoid. Acquiring the lease after the body is
/// deserialized costs one parse of a bounded payload and gets the correct partition.
/// </para>
/// <para>
/// The numbers are §E.9 verbatim. Resolve is a fixed window of five per hour per installation,
/// because a correct SDK calls it once in the lifetime of an installation and five leaves room for
/// four retries. Events is a token bucket of sixty a minute with a burst of a hundred and twenty,
/// which lets an application that was offline flush its queue in one go and then settle. Credential
/// verification is limited per caller address, because that limit protects the Argon2id work
/// factor rather than any one installation.
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
    private readonly PartitionedRateLimiter<string> _authentication;
    private readonly PartitionedRateLimiter<string> _claimCodeIssue;
    private bool _disposed;

    /// <summary>
    /// Creates the limiters.
    /// </summary>
    /// <param name="options">The configured limits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public AttributionRateLimiters(IOptions<AttributionRateLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AttributionRateLimitOptions limits = options.Value;

        _resolve = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.ResolvePermitLimit,
                Window = TimeSpan.FromMinutes(limits.ResolveWindowMinutes),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        _events = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = limits.EventsBurstLimit,
                TokensPerPeriod = limits.EventsTokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(limits.EventsPeriodSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        _authentication = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = limits.AuthenticationPermitLimit,
                TokensPerPeriod = limits.AuthenticationPermitLimit,
                ReplenishmentPeriod = TimeSpan.FromSeconds(limits.AuthenticationWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        _claimCodeIssue = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.ClaimCodeIssuePermitLimit,
                Window = TimeSpan.FromSeconds(limits.ClaimCodeIssueWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

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
    /// minute with a hundred events each would be six thousand events a minute, which is not the
    /// limit §E.9 intends.
    /// </remarks>
    public ValueTask<RateLimitLease> AcquireEventsAsync(string installId, int permits, CancellationToken cancellationToken) =>
        _events.AcquireAsync(installId, Math.Max(permits, 1), cancellationToken);

    /// <summary>Takes one permit from the credential verification limit of a caller.</summary>
    /// <param name="callerKey">Coarse caller identity, normally a truncated network prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireAuthenticationAsync(string callerKey, CancellationToken cancellationToken) =>
        _authentication.AcquireAsync(callerKey, 1, cancellationToken);

    /// <summary>Takes one permit from the claim code issuance limit of a caller.</summary>
    /// <param name="callerKey">Coarse caller identity, normally the tenant that asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease.</returns>
    public ValueTask<RateLimitLease> AcquireClaimCodeIssueAsync(string callerKey, CancellationToken cancellationToken) =>
        _claimCodeIssue.AcquireAsync(callerKey, 1, cancellationToken);

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
        _authentication.Dispose();
        _claimCodeIssue.Dispose();
    }
}
