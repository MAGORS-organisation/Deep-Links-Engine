using System.Collections.Concurrent;
using System.Net;

using Dle.Edge.Configuration;

using Microsoft.Extensions.Options;

using UAParser;

namespace Dle.Edge.Clients;

/// <summary>
/// Turns a transport-neutral <see cref="ClientRequest"/> into the <see cref="ClientContext"/> the
/// routing engine and the click stream both consume (§B.6.1 step 3).
/// </summary>
/// <remarks>
/// <para>
/// The classification budget in §B.6.1 is 1.5 ms for user agent parsing and the geographic lookup
/// together, out of a 5.5 ms p50. User agent parsing is a cascade of regular expressions and would
/// eat most of that on its own, so the parse result is memoised: real traffic sends a few thousand
/// distinct user agent strings, and the same handful account for the overwhelming majority of
/// requests (§C.2).
/// </para>
/// <para>
/// The memo is keyed by a 64-bit FNV-1a hash of the string rather than by the string itself. That
/// bounds the memory a cached entry costs regardless of how long a client makes its user agent, and
/// the cache is capacity-bounded on top of that because the key space is attacker controlled — a
/// client that sends a fresh user agent per request must not be able to grow a dictionary without
/// limit.
/// </para>
/// <para>
/// Crawler <em>detection</em> happens here, crawler <em>confirmation</em> does not: confirming a
/// claim needs DNS, and this interface is synchronous by contract. The pipeline calls
/// <see cref="IBotVerifier"/> afterwards and rewrites the two flags, which is what TC-107 checks.
/// </para>
/// </remarks>
public sealed class ClientClassifier : IClientClassifier
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly Parser _parser;
    private readonly IGeoIpResolver _geoIp;
    private readonly int _capacity;
    private readonly ConcurrentDictionary<ulong, UserAgentFacts> _memo = new();

    /// <summary>
    /// Creates the classifier.
    /// </summary>
    /// <param name="geoIp">Geographic resolver; may be permanently unavailable (§D.6).</param>
    /// <param name="options">Edge options; supplies the memo capacity.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ClientClassifier(IGeoIpResolver geoIp, IOptions<EdgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(geoIp);
        ArgumentNullException.ThrowIfNull(options);

        _geoIp = geoIp;
        _capacity = options.Value.UserAgentCacheCapacity;

        // The compiled regular expressions cost more to build and are markedly faster to run, which
        // is the right trade for a process that parses user agents for its whole lifetime. The match
        // timeout is the guard against a pathological string: a regular expression cascade over
        // attacker-controlled input is a denial-of-service primitive without one.
        _parser = Parser.GetDefault(new ParserOptions
        {
            UseCompiledRegex = true,
            MatchTimeOut = TimeSpan.FromMilliseconds(100),
        });
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public ClientContext Classify(ClientRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        UserAgentFacts facts = Describe(request.UserAgent);
        GeoLocation? location = _geoIp.Resolve(request.RemoteIp);

        return new ClientContext
        {
            Platform = facts.Platform,
            DeviceClass = facts.DeviceClass,
            Channel = facts.Channel,
            UaFamily = facts.UaFamily,
            OsFamily = facts.OsFamily,
            OsVersion = facts.OsVersion,
            AppVersion = facts.AppVersion,
            Country = location?.Country,
            Region = location?.Region,
            Language = PrimaryLanguage(request.AcceptLanguage),
            ReferrerHost = ReferrerHost(request.Referrer),
            IsCrawler = facts.CrawlerName is not null,
            IsSpoofedBot = false,
            CrawlerName = facts.CrawlerName,
            RemoteIp = request.RemoteIp,
            Query = request.Query,
            ReceivedAt = request.ReceivedAt,
        };
    }

    /// <summary>
    /// Returns the parsed facts about one user agent string, parsing it at most once per capacity
    /// generation.
    /// </summary>
    private UserAgentFacts Describe(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return UserAgentFacts.Unknown;
        }

        ulong key = Hash(userAgent);

        if (_memo.TryGetValue(key, out UserAgentFacts? cached))
        {
            return cached;
        }

        UserAgentFacts facts = Parse(userAgent);

        // A generational reset rather than an eviction policy. Least-recently-used bookkeeping on the
        // hot path would cost more than the parse it saves, and the working set of real user agents
        // is far below the capacity, so a reset is a rare event that costs one re-parse per live
        // string. Under a deliberate flood it is exactly the intended behaviour: bounded memory,
        // degraded to no memoisation.
        if (_memo.Count >= _capacity)
        {
            _memo.Clear();
        }

        _ = _memo.TryAdd(key, facts);
        return facts;
    }

    private UserAgentFacts Parse(string userAgent)
    {
        if (CrawlerCatalog.TryMatch(userAgent, out string crawler))
        {
            return new UserAgentFacts
            {
                Platform = Platform.Other,
                DeviceClass = DeviceClass.Bot,
                Channel = ClientChannel.Crawler,
                UaFamily = crawler,
                CrawlerName = crawler,
            };
        }

        ClientInfo parsed = _parser.Parse(userAgent);
        Platform platform = ReadPlatform(parsed.OS.Family);
        ClientChannel channel = WebViewCatalog.Classify(userAgent);

        return new UserAgentFacts
        {
            Platform = platform,
            DeviceClass = ReadDeviceClass(parsed, platform, userAgent),
            Channel = channel,
            UaFamily = Blank(parsed.UA.Family),
            OsFamily = Blank(parsed.OS.Family),
            OsVersion = ReadOsVersion(parsed.OS),
            AppVersion = WebViewCatalog.ReadSdkVersion(userAgent),
            CrawlerName = parsed.Device.IsSpider ? "unknown" : null,
        };
    }

    /// <summary>
    /// Maps the operating system family reported by the parser onto the routing platform.
    /// </summary>
    /// <remarks>
    /// The families are matched by prefix because the parser reports variants — "Android",
    /// "Android 12", "Windows 10" — and a routing rule only ever asks the coarse question.
    /// </remarks>
    private static Platform ReadPlatform(string? osFamily)
    {
        if (string.IsNullOrEmpty(osFamily))
        {
            return Platform.Unknown;
        }

        if (osFamily.StartsWith("iOS", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("iPadOS", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("watchOS", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("tvOS", StringComparison.OrdinalIgnoreCase))
        {
            return Platform.Ios;
        }

        if (osFamily.StartsWith("Android", StringComparison.OrdinalIgnoreCase))
        {
            return Platform.Android;
        }

        if (osFamily.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("Mac OS", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("macOS", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("Linux", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("Fedora", StringComparison.OrdinalIgnoreCase) ||
            osFamily.StartsWith("Chrome OS", StringComparison.OrdinalIgnoreCase))
        {
            return Platform.Desktop;
        }

        return Platform.Other;
    }

    private static DeviceClass ReadDeviceClass(ClientInfo parsed, Platform platform, string userAgent)
    {
        if (parsed.Device.IsSpider)
        {
            return DeviceClass.Bot;
        }

        string family = parsed.Device.Family ?? string.Empty;

        if (family.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
            family.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
            family.Contains("Kindle", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceClass.Tablet;
        }

        if (family.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            family.Contains("iPod", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceClass.Phone;
        }

        // Android says nothing about form factor in the device family, but every Android phone
        // browser sends the "Mobile" product token and every Android tablet omits it. That is the
        // documented convention and it is the only signal available without client hints.
        if (platform == Platform.Android)
        {
            return userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
                ? DeviceClass.Phone
                : DeviceClass.Tablet;
        }

        return platform switch
        {
            Platform.Ios => DeviceClass.Phone,
            Platform.Desktop => DeviceClass.Desktop,
            _ => DeviceClass.Unknown,
        };
    }

    /// <summary>
    /// Joins the operating system version parts into the form <see cref="VersionComparer"/> compares.
    /// </summary>
    private static string? ReadOsVersion(OS os)
    {
        if (string.IsNullOrEmpty(os.Major))
        {
            return null;
        }

        string raw = string.IsNullOrEmpty(os.Minor)
            ? os.Major
            : string.IsNullOrEmpty(os.Patch)
                ? string.Concat(os.Major, ".", os.Minor)
                : string.Concat(os.Major, ".", os.Minor, ".", os.Patch);

        return VersionComparer.TryNormalize(raw, out string normalized) ? normalized : null;
    }

    /// <summary>
    /// Reads the primary language subtag from <c>Accept-Language</c>, lowercased.
    /// </summary>
    /// <remarks>
    /// Only the first entry is considered and its quality value is ignored. The header is ordered by
    /// preference in practice, and a routing rule matches a bare subtag such as <c>sk</c>, so parsing
    /// the full <c>q</c> ranking would cost allocations on the hot path for a distinction no rule can
    /// express.
    /// </remarks>
    private static string? PrimaryLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrEmpty(acceptLanguage))
        {
            return null;
        }

        ReadOnlySpan<char> span = acceptLanguage.AsSpan();
        int end = span.IndexOfAny(',', ';');

        if (end >= 0)
        {
            span = span[..end];
        }

        span = span.Trim();

        int dash = span.IndexOf('-');

        if (dash >= 0)
        {
            span = span[..dash];
        }

        if (span.Length is 0 or > 8)
        {
            return null;
        }

        foreach (char c in span)
        {
            if (!char.IsAsciiLetter(c))
            {
                return null;
            }
        }

        return span.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Reduces the referrer to its host.
    /// </summary>
    /// <remarks>
    /// Only the host is ever kept. SHARED-KERNEL §17.5 forbids retaining a full referrer, which can
    /// carry a path and a query string belonging to a page the visitor was reading, and the host is
    /// the only part any report or routing rule uses.
    /// </remarks>
    private static string? ReferrerHost(string? referrer)
    {
        if (string.IsNullOrEmpty(referrer))
        {
            return null;
        }

        if (!Uri.TryCreate(referrer, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return HostNormalizer.TryNormalize(uri.Host, out string normalized) ? normalized : null;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "Other", StringComparison.Ordinal) ? null : value;

    /// <summary>
    /// FNV-1a over the UTF-16 code units of the user agent.
    /// </summary>
    /// <remarks>
    /// Not a cryptographic hash and not meant to be: the value never leaves the process and a
    /// collision costs one misclassified user agent, not a security property. It is used instead of
    /// the string itself so that a cached entry has a fixed size no matter how long the header was.
    /// </remarks>
    private static ulong Hash(string value)
    {
        ulong hash = FnvOffsetBasis;

        foreach (char c in value)
        {
            hash = (hash ^ (byte)c) * FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
        }

        return hash;
    }
}

/// <summary>
/// Everything the classifier derives from a user agent string alone, memoised per distinct string.
/// </summary>
/// <remarks>
/// Deliberately excludes anything request-specific — address, language, referrer — because those
/// vary per request and caching them against a user agent would leak one visitor's context into
/// another's.
/// </remarks>
public sealed record UserAgentFacts
{
    /// <summary>Facts for a request that sent no user agent at all.</summary>
    public static UserAgentFacts Unknown { get; } = new();

    /// <summary>Routing platform.</summary>
    public Platform Platform { get; init; } = Platform.Unknown;

    /// <summary>Form factor.</summary>
    public DeviceClass DeviceClass { get; init; } = DeviceClass.Unknown;

    /// <summary>Channel the request came through.</summary>
    public ClientChannel Channel { get; init; } = ClientChannel.Unknown;

    /// <summary>Browser family, or the crawler name for a crawler.</summary>
    public string? UaFamily { get; init; }

    /// <summary>Operating system family.</summary>
    public string? OsFamily { get; init; }

    /// <summary>Normalized operating system version.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Application version, when a DLE SDK identified itself in the user agent.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Canonical crawler name the user agent claims to be, if any.</summary>
    public string? CrawlerName { get; init; }
}
