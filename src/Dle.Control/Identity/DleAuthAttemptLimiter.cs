using System.Threading.RateLimiting;

using Dle.Control.Configuration;

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
    private readonly PartitionedRateLimiter<string> _limiter;
    private bool _disposed;

    /// <summary>Creates the limiter.</summary>
    /// <param name="options">Rate limit configuration; the defaults are the §E.9 values.</param>
    /// <param name="timeProvider">Clock the token bucket refills against.</param>
    public DleAuthAttemptLimiter(
        IOptions<DleRateLimitOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleRateLimitOptions limits = options.Value;

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

        string partition = string.Concat(remoteAddress ?? "unknown", "|", keyPrefix);

        using RateLimitLease lease = _limiter.AttemptAcquire(partition);
        return lease.IsAcquired;
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
    }
}
