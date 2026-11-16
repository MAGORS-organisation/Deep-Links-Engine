using System.Threading.RateLimiting;

using Dle.Control.Configuration;
using Dle.Persistence.Fast.Caching;

using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// The authentication attempt limiter of §E.9: ten attempts a minute per caller, with constant-time
/// verification either way.
/// </summary>
/// <remarks>
/// <para>
/// This limiter sits inside the authentication handlers rather than on the endpoint pipeline, and
/// that placement is the point. Endpoint rate limiting runs after authentication, so it cannot stop
/// an attacker from making the server perform one Argon2id verification per guess — which is both
/// the credential-stuffing surface and, at roughly 19 MiB of memory per verification, a cheap way to
/// exhaust the process.
/// </para>
/// <para>
/// The partition key is the remote address together with the presented key prefix. Keying on the
/// address alone would let one noisy client lock out everyone behind the same NAT; keying on the
/// prefix alone would let an attacker rotate prefixes freely. Both together bound the guesses
/// against any one key from any one place.
/// </para>
/// <para>
/// A refused attempt costs the same as an accepted one from the caller's point of view: the handler
/// still reports "the credential was refused" and never distinguishes "unknown key" from "too many
/// attempts" in the time it takes to answer (T-07).
/// </para>
/// </remarks>
public sealed class DleAuthAttemptLimiter : IDisposable
{
    /// <summary>
    /// How many times the per-key budget one network prefix gets in total. Several legitimate keys
    /// can sit behind one address; an unbounded number of invented ones cannot.
    /// </summary>
    private const int AddressBudgetMultiplier = 6;

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly PartitionedRateLimiter<string> _byAddress;
    private readonly ValkeyRateLimitStore? _distributedStore;
    private readonly int _keyBurst;
    private readonly int _keyPerMinute;
    private readonly int _addressBurst;
    private readonly int _addressPerMinute;
    private bool _disposed;

    /// <summary>Creates the limiter.</summary>
    /// <param name="options">Rate limit configuration; the defaults are the §E.9 values.</param>
    /// <param name="timeProvider">Clock the token bucket refills against.</param>
    /// <param name="distributedStore">Optional Valkey store to share attempt budgets across control-plane replicas.</param>
    public DleAuthAttemptLimiter(
        IOptions<DleRateLimitOptions> options,
        TimeProvider timeProvider,
        ValkeyRateLimitStore? distributedStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleRateLimitOptions limits = options.Value;
        _distributedStore = distributedStore;
        _keyBurst = limits.AuthAttemptBurst;
        _keyPerMinute = limits.AuthAttemptsPerMinute;
        _addressBurst = limits.AuthAttemptBurst * AddressBudgetMultiplier;
        _addressPerMinute = limits.AuthAttemptsPerMinute * AddressBudgetMultiplier;

        _limiter = PartitionedRateLimiter.Create<string, string>(
            partitionKey => RateLimitPartition.GetTokenBucketLimiter(
                partitionKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.AuthAttemptBurst,
                    TokensPerPeriod = limits.AuthAttemptsPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }),
            StringComparer.Ordinal);

        // The key prefix in the partition above is whatever the caller sent, so a caller that
        // invents a new prefix per request gets a new bucket per request, and every one of them
        // buys a full Argon2id verification. This second budget is keyed on the address alone.
        _byAddress = PartitionedRateLimiter.Create<string, string>(
            partitionKey => RateLimitPartition.GetTokenBucketLimiter(
                partitionKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.AuthAttemptBurst * AddressBudgetMultiplier,
                    TokensPerPeriod = limits.AuthAttemptsPerMinute * AddressBudgetMultiplier,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Takes one attempt from the caller's budget.
    /// </summary>
    /// <param name="remoteAddress">Caller address, already reduced to a coarse prefix.</param>
    /// <param name="keyPrefix">Non-secret prefix of the presented key.</param>
    /// <returns><see langword="true"/> when the attempt may proceed to verification.</returns>
    public bool TryAcquire(string? remoteAddress, string keyPrefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string address = remoteAddress ?? "unknown";

        using RateLimitLease byAddress = _byAddress.AttemptAcquire(address);

        if (!byAddress.IsAcquired)
        {
            return false;
        }

        using RateLimitLease lease = _limiter.AttemptAcquire(string.Concat(address, "|", keyPrefix));
        return lease.IsAcquired;
    }

    /// <summary>
    /// Takes one attempt from the caller's distributed budget (falling back to in-process limits
    /// when Valkey is unconfigured or unavailable).
    /// </summary>
    /// <param name="remoteAddress">Caller address, already reduced to a coarse prefix.</param>
    /// <param name="keyPrefix">Non-secret prefix of the presented key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the attempt may proceed to verification.</returns>
    public async ValueTask<bool> TryAcquireAsync(
        string? remoteAddress,
        string keyPrefix,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string address = remoteAddress ?? "unknown";

        if (_distributedStore is { IsAvailable: true })
        {
            TimeSpan period = TimeSpan.FromMinutes(1);
            ValkeyRateLimitDecision? byAddress = await _distributedStore.TryAcquireTokenBucketAsync(
                "auth:addr:" + address,
                _addressBurst,
                _addressPerMinute,
                period,
                cost: 1,
                cancellationToken).ConfigureAwait(false);

            if (byAddress is { } addressResult)
            {
                if (!addressResult.IsAcquired)
                {
                    return false;
                }

                ValkeyRateLimitDecision? byKey = await _distributedStore.TryAcquireTokenBucketAsync(
                    string.Concat("auth:key:", address, "|", keyPrefix),
                    _keyBurst,
                    _keyPerMinute,
                    period,
                    cost: 1,
                    cancellationToken).ConfigureAwait(false);

                if (byKey is { } keyResult)
                {
                    return keyResult.IsAcquired;
                }
            }
        }

        return TryAcquire(remoteAddress, keyPrefix);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _limiter.Dispose();
        _byAddress.Dispose();
    }
}
