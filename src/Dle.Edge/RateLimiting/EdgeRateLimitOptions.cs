using System.ComponentModel.DataAnnotations;

namespace Dle.Edge.RateLimiting;

/// <summary>
/// The edge half of <c>Dle:RateLimits</c>. Defaults are the values in §E.9 verbatim.
/// </summary>
/// <remarks>
/// <para>
/// The two resolve limits are separate on purpose and that separation is the single most important
/// property in the table. A campaign that goes viral produces a burst of <em>successful</em> resolves;
/// if the anti-enumeration budget shared a counter with it, the burst would exhaust the budget and
/// the enumeration defence (T-07) would switch itself off exactly when the service is most visible.
/// </para>
/// <para>
/// Both are keyed by network prefix rather than by address — /24 for IPv4, /48 for IPv6 — because an
/// attacker with a single IPv6 allocation otherwise has 2^80 free buckets.
/// </para>
/// </remarks>
public sealed class EdgeRateLimitOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:RateLimits:Edge";

    /// <summary>
    /// Whether the limiters are installed. Off is only ever right for a load test that is measuring
    /// something else.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sliding window over every resolve request, keyed by network prefix.</summary>
    public ResolveRateLimitOptions Resolve { get; set; } = new();

    /// <summary>Token bucket over resolve requests that ended in 404, keyed by network prefix.</summary>
    public NotFoundRateLimitOptions NotFound { get; set; } = new();

    /// <summary>Sliding window over QR rendering, keyed by address.</summary>
    public QrRateLimitOptions Qr { get; set; } = new();
}

/// <summary>
/// Sliding window for <c>GET /{slug}</c>, bound from <c>Dle:RateLimits:Edge:Resolve</c>.
/// </summary>
public sealed class ResolveRateLimitOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:RateLimits:Edge:Resolve";

    /// <summary>Requests permitted per window. §E.9 default: 600.</summary>
    [Range(1, 1_000_000)]
    public int PermitsPerWindow { get; set; } = 600;

    /// <summary>Window length in seconds. §E.9 default: 60.</summary>
    [Range(1, 3_600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Segments the window is divided into. More segments make the window slide more smoothly at the
    /// cost of one counter each.
    /// </summary>
    [Range(1, 60)]
    public int SegmentsPerWindow { get; set; } = 6;
}

/// <summary>
/// Token bucket and shadow ban for resolve requests that ended in 404, bound from
/// <c>Dle:RateLimits:Edge:NotFound</c> (§E.9, T-07, TC-108).
/// </summary>
public sealed class NotFoundRateLimitOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:RateLimits:Edge:NotFound";

    /// <summary>Tokens replenished per period. §E.9 default: 20 per minute.</summary>
    [Range(1, 100_000)]
    public int TokensPerPeriod { get; set; } = 20;

    /// <summary>Replenishment period in seconds. §E.9 default: 60.</summary>
    [Range(1, 3_600)]
    public int ReplenishmentPeriodSeconds { get; set; } = 60;

    /// <summary>Bucket capacity, which is the permitted burst. §E.9 default: 40.</summary>
    [Range(1, 100_000)]
    public int Burst { get; set; } = 40;

    /// <summary>
    /// How long a prefix stays shadow banned once it has drained the bucket. §E.9 default: 15 minutes.
    /// </summary>
    /// <remarks>
    /// A shadow banned prefix is answered with the same 404 body as any other miss and the database is
    /// not consulted, so the scan costs the attacker exactly as much as before and costs the service
    /// nothing.
    /// </remarks>
    [Range(1, 1_440)]
    public int ShadowBanMinutes { get; set; } = 15;

    /// <summary>
    /// How many prefixes are tracked before the least recently seen entries are evicted.
    /// </summary>
    [Range(64, 10_000_000)]
    public int TrackedPrefixCapacity { get; set; } = 50_000;
}

/// <summary>
/// Sliding window for <c>GET /{slug}/qr</c>, bound from <c>Dle:RateLimits:Edge:Qr</c>.
/// </summary>
/// <remarks>
/// QR rendering is a rasterisation, not a lookup, so its limit is an order of magnitude tighter than
/// the resolve limit and is keyed by the full address rather than by prefix, matching §E.9.
/// </remarks>
public sealed class QrRateLimitOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:RateLimits:Edge:Qr";

    /// <summary>Requests permitted per window. §E.9 default: 30.</summary>
    [Range(1, 100_000)]
    public int PermitsPerWindow { get; set; } = 30;

    /// <summary>Window length in seconds. §E.9 default: 60.</summary>
    [Range(1, 3_600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Segments the window is divided into.</summary>
    [Range(1, 60)]
    public int SegmentsPerWindow { get; set; } = 6;
}
