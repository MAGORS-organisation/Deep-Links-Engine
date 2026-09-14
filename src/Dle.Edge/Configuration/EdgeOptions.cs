using System.ComponentModel.DataAnnotations;

namespace Dle.Edge.Configuration;

/// <summary>
/// Everything the edge reads from the <c>Dle:Edge</c> configuration section (SHARED-KERNEL §16, §C.4).
/// </summary>
/// <remarks>
/// <para>
/// The subsections are separate option types bound from their own sections rather than nested
/// properties of this one. That is deliberate: <c>ValidateDataAnnotations</c> does not recurse into
/// a nested object, so a nested <c>AutoRedirectMs</c> of zero would pass validation and only fail in
/// production. Each type is registered with its own <c>ValidateOnStart</c>, so every value in
/// §16 is checked before the first request and the diagnostic names the section it came from.
/// </para>
/// <para>
/// <c>Dle:Edge:Cache</c> is deliberately absent. It is bound by <c>Dle.Persistence.Fast</c>, which
/// owns the two-level cache, and duplicating it here would create a second source of truth for one
/// key.
/// </para>
/// </remarks>
public sealed class EdgeOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge";

    /// <summary>
    /// How many distinct user agent strings the classifier keeps parsed results for.
    /// </summary>
    /// <remarks>
    /// User agent parsing is a regular expression cascade and it sits on the hot path (§C.2), so the
    /// result is memoised. The cache is bounded because the key space is attacker controlled: a
    /// client can send a fresh user agent on every request, and an unbounded dictionary would be a
    /// memory exhaustion primitive rather than an optimisation.
    /// </remarks>
    [Range(256, 1_000_000)]
    public int UserAgentCacheCapacity { get; set; } = 20_000;

    /// <summary>
    /// Query parameter that switches a request into decision preview mode (FR-166).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z_][a-z0-9_]{0,31}$")]
    public string PreviewQueryKey { get; set; } = "_dl";

    /// <summary>Value of <see cref="PreviewQueryKey"/> that requests preview mode.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PreviewQueryValue { get; set; } = "preview";
}

/// <summary>
/// Interstitial behaviour, bound from <c>Dle:Edge:Interstitial</c>.
/// </summary>
/// <remarks>
/// The interstitial exists because an in-app webview only hands a universal link to the operating
/// system on a real tap; a scripted navigation is not a user gesture (§A.2.6). Switching it off is
/// supported for a deployment that only ever serves ordinary browsers, and it degrades to a plain
/// redirect.
/// </remarks>
public sealed class InterstitialOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:Interstitial";

    /// <summary>Whether an interstitial may be served at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Delay before the interstitial falls back to the store or web target, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Below roughly half a second the fallback fires before the operating system has had a chance
    /// to hand the deep link to an installed application, which turns every open into a store visit.
    /// </remarks>
    [Range(300, 30_000)]
    public int AutoRedirectMs { get; set; } = 1_200;

    /// <summary>Where the interstitial takes its branding from: <c>tenant</c> or <c>none</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(tenant|none)$")]
    public string Branding { get; set; } = "tenant";
}

/// <summary>
/// Crawler verification behaviour, bound from <c>Dle:Edge:BotDetection</c>.
/// </summary>
public sealed class BotDetectionOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:BotDetection";

    /// <summary>
    /// Whether a user agent claiming to be a crawler is confirmed by reverse DNS (FR-161, TC-107).
    /// </summary>
    /// <remarks>
    /// Turning this off makes <c>is_bot</c> a claim rather than a fact, which is acceptable in a
    /// closed test environment and nowhere else: the flag decides whether a click counts towards a
    /// campaign, so an unverified one is a free way to inflate or suppress somebody else's numbers.
    /// </remarks>
    public bool ReverseDnsVerify { get; set; } = true;

    /// <summary>How long a verification verdict stays valid.</summary>
    [Range(1, 1_440)]
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>How many verification verdicts are remembered.</summary>
    [Range(64, 1_000_000)]
    public int CacheCapacity { get; set; } = 20_000;

    /// <summary>
    /// Budget for the two DNS lookups behind one verification, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The lookup only runs for a request whose user agent claims to be a crawler, and only on a
    /// cache miss, but it is still I/O on the resolve path, so it is bounded rather than trusted.
    /// Exceeding the budget is a failed verification, never a slow response (SHARED-KERNEL §17.8,
    /// §17.9).
    /// </remarks>
    [Range(50, 5_000)]
    public int VerificationTimeoutMs { get; set; } = 750;
}

/// <summary>
/// Geographic lookup behaviour, bound from <c>Dle:Edge:GeoIp</c>.
/// </summary>
/// <remarks>
/// NFR-14 is absolute: the resolve path makes no outbound third-party call, geographic lookup
/// included. The database is therefore a local file opened in memory-mapped mode, and
/// <see cref="AutoUpdate"/> means "notice that the file on disk was replaced", not "download it".
/// Refreshing the file itself belongs to the deployment — a sidecar, a cron job or a mounted volume.
/// </remarks>
public sealed class GeoIpOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:GeoIp";

    /// <summary>Provider identifier. Only <c>MaxMindMmap</c> and <c>None</c> are implemented.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(MaxMindMmap|None)$")]
    public string Provider { get; set; } = "MaxMindMmap";

    /// <summary>
    /// Path to the GeoLite2 or GeoIP2 database file. A missing or unreadable file is not an error:
    /// country becomes <see langword="null"/>, geo-dependent rules fall through to the default rule
    /// and a metric is raised instead (§D.6).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Whether the background job watches the file and swaps in a replaced database.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>How often the background job looks at the file.</summary>
    [Range(1, 1_440)]
    public int RefreshMinutes { get; set; } = 60;
}

/// <summary>
/// Response header policy, bound from <c>Dle:Edge:Security</c> (S-04, T-11).
/// </summary>
public sealed class SecurityHeaderOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:Security";

    /// <summary>Whether the security headers are written. Only a test harness turns this off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>max-age</c> of the HSTS header, in seconds. The header is only written for a request that
    /// arrived over TLS, so a plain-HTTP development host never pins itself.
    /// </summary>
    [Range(0, 63_072_000)]
    public int HstsMaxAgeSeconds { get; set; } = 31_536_000;

    /// <summary>Whether the HSTS header covers subdomains.</summary>
    public bool HstsIncludeSubDomains { get; set; } = true;

    /// <summary>
    /// Sources the interstitial may load images from, in addition to the page origin.
    /// </summary>
    /// <remarks>
    /// Open Graph images are usually served from the tenant's own content delivery network, so the
    /// default allows any HTTPS origin for images and nothing else. Everything that can execute —
    /// script, style, frame, connect — is restricted to the page origin plus a per-response nonce,
    /// with no <c>unsafe-inline</c> anywhere (T-11).
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string ImageSources { get; set; } = "https: data:";
}

/// <summary>
/// Reverse proxy handling, bound from <c>Dle:Edge:Network</c>.
/// </summary>
/// <remarks>
/// The client address is not cosmetic here. It is the rate limiting partition key (§E.9), the input
/// to the hashed identifier in the click stream (FR-247) and the geographic lookup key, so believing
/// a forwarded header from an untrusted peer would let a client pick its own rate limit bucket. The
/// header is therefore only honoured when the immediate peer is a configured proxy.
/// </remarks>
public sealed class NetworkOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Edge:Network";

    /// <summary>Whether <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honoured.</summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>How many proxy hops to walk back through the forwarded chain.</summary>
    [Range(1, 16)]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Addresses of trusted proxies.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Trusted proxy networks in CIDR notation, for example <c>10.0.0.0/8</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];
}
