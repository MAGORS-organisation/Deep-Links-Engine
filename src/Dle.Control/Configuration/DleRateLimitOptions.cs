using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Rate limits for the control plane, bound from <c>Dle:RateLimits</c>. The defaults are the values
/// tabulated in §E.9.
/// </summary>
/// <remarks>
/// The numbers are configuration because a self-hosted instance serving one marketing team and a
/// hosted instance serving hundreds genuinely need different thresholds. What is not configurable is
/// that the limits exist: every route carries a named limiter, and the global limiter refuses
/// anything that reaches it without one.
/// </remarks>
public sealed class DleRateLimitOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:RateLimits";

    /// <summary>Link creations per minute for one API key (§E.9).</summary>
    [Range(1, 100_000)]
    public int LinkCreatePerMinute { get; set; } = 60;

    /// <summary>
    /// Link creations per minute for a tenant younger than <see cref="NewTenantDays"/> (§E.9).
    /// </summary>
    /// <remarks>
    /// A freshly provisioned tenant creating links as fast as the API allows is the shape of a
    /// throwaway account mass-producing phishing short URLs; an established tenant doing the same is
    /// a campaign import. The two therefore get different budgets.
    /// </remarks>
    [Range(1, 100_000)]
    public int NewTenantLinkCreatePerMinute { get; set; } = 10;

    /// <summary>How many days a tenant counts as new for the stricter limit above (§E.9).</summary>
    [Range(0, 365)]
    public int NewTenantDays { get; set; } = 7;

    /// <summary>Concurrent bulk batches permitted per tenant (§E.9).</summary>
    [Range(1, 64)]
    public int BulkConcurrentBatches { get; set; } = 2;

    /// <summary>Queued bulk batches per tenant before further requests are refused.</summary>
    [Range(0, 64)]
    public int BulkQueuedBatches { get; set; }

    /// <summary>Authentication attempts per minute for one caller (§E.9).</summary>
    /// <remarks>
    /// Keyed on the remote address together with the presented key prefix, so one noisy client
    /// cannot lock another out, and the verification itself stays constant time whether or not the
    /// limit has been reached.
    /// </remarks>
    [Range(1, 10_000)]
    public int AuthAttemptsPerMinute { get; set; } = 10;

    /// <summary>Burst size of the authentication token bucket.</summary>
    [Range(1, 10_000)]
    public int AuthAttemptBurst { get; set; } = 10;

    /// <summary>Read requests per minute for one credential.</summary>
    [Range(1, 1_000_000)]
    public int ReadPerMinute { get; set; } = 600;

    /// <summary>Administrative writes other than link creation, per minute per credential.</summary>
    [Range(1, 1_000_000)]
    public int WritePerMinute { get; set; } = 120;

    /// <summary>Seconds written into <c>Retry-After</c> when a limiter refuses a request.</summary>
    [Range(1, 3600)]
    public int RetryAfterSeconds { get; set; } = 60;
}
