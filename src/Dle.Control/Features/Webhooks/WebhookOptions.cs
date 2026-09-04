using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Configuration of the webhook module, bound from <c>Dle:Webhooks</c> (SHARED-KERNEL §16, §B.7.4).
/// </summary>
/// <remarks>
/// <para>
/// The retry schedule is the part worth reading twice. A customer endpoint that is down for ten
/// minutes must not lose its deliveries, and a customer endpoint that is down for two days must
/// not be able to hold the queue hostage. Exponential backoff with jitter and a bounded attempt
/// count is how both are true at once; what is left over lands in the dead letter queue, which is
/// visible as <c>dle_webhook_dlq_size</c> precisely so that a silent loss becomes a loud one
/// (§C.6).
/// </para>
/// <para>
/// The tolerance is five minutes, matching §B.7.4 and T-13. It is published here rather than
/// hidden in the signer because the receiver has to apply the same number: a timestamp older than
/// the tolerance must be refused by the receiver even when the signature verifies, or the
/// signature is a replayable token rather than a proof of freshness.
/// </para>
/// </remarks>
public sealed class WebhookOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Webhooks";

    /// <summary>
    /// How many times one delivery is attempted before it is dead lettered.
    /// </summary>
    [Range(1, 32)]
    public int MaxAttempts { get; set; } = 6;

    /// <summary>Base of the exponential backoff, in seconds.</summary>
    /// <remarks>
    /// Attempt <c>n</c> waits <c>BaseDelaySeconds * 2^(n-1)</c>, capped at
    /// <see cref="MaxDelaySeconds"/> and then spread by jitter. With the defaults that is roughly
    /// 5 s, 10 s, 20 s, 40 s, 80 s and 160 s — about four and a half minutes of patience, which
    /// covers a deploy but not an outage.
    /// </remarks>
    [Range(1, 3600)]
    public int BaseDelaySeconds { get; set; } = 5;

    /// <summary>Upper bound of one backoff interval, in seconds.</summary>
    [Range(1, 86400)]
    public int MaxDelaySeconds { get; set; } = 3600;

    /// <summary>
    /// Fraction of the computed delay that is randomised, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// Without jitter, a customer endpoint that fails for every tenant at once gets every retry of
    /// every tenant back at the same instant, which is a self-inflicted thundering herd on an
    /// endpoint that is already struggling.
    /// </remarks>
    [Range(0d, 1d)]
    public double JitterRatio { get; set; } = 0.2d;

    /// <summary>Timeout of one delivery attempt, in seconds.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Timeout for establishing the TCP connection of a delivery, in seconds.</summary>
    [Range(1, 60)]
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// How stale a signature may be before a receiver should refuse it, in minutes (§B.7.4).
    /// </summary>
    [Range(1, 60)]
    public int SignatureToleranceMinutes { get; set; } = 5;

    /// <summary>How many deliveries one dispatcher pass claims.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>How often the dispatcher looks for due deliveries, in seconds.</summary>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Largest number of subscriptions one tenant may register.</summary>
    /// <remarks>
    /// A soft quota in the sense of §E.9: exceeding it blocks creating another subscription and
    /// never blocks a delivery that already exists.
    /// </remarks>
    [Range(1, 1000)]
    public int MaxSubscriptionsPerTenant { get; set; } = 20;

    /// <summary>Largest number of deliveries a customer may list in one request.</summary>
    [Range(1, 1000)]
    public int MaxDeliveryPageSize { get; set; } = 100;

    /// <summary>
    /// Whether a subscription may point at a host that is not resolvable to a public address.
    /// </summary>
    /// <remarks>
    /// Off, and it should stay off outside a test harness. The check is what stops a webhook from
    /// being aimed at <c>169.254.169.254</c> and turning the control plane into a proxy for the
    /// cloud metadata service (T-02).
    /// </remarks>
    public bool AllowPrivateDestinations { get; set; }

    /// <summary>Backoff before attempt number <paramref name="attempt"/>.</summary>
    /// <param name="attempt">Number of attempts already made, starting at one.</param>
    /// <returns>The un-jittered delay.</returns>
    public TimeSpan DelayFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        // Doubling in a long and clamping before the conversion, so a large attempt count cannot
        // overflow into a negative delay and schedule a retry in the past.
        int exponent = Math.Min(attempt - 1, 30);
        long seconds = Math.Min((long)BaseDelaySeconds << exponent, MaxDelaySeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}
