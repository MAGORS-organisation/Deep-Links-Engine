namespace Dle.Domain.Clients;

/// <summary>
/// A classified client. This is the input to rule evaluation and, after the consent gate has had
/// its say, to the click stream.
/// </summary>
/// <remarks>
/// Every field is already minimised: the parsed user agent families instead of the raw header, the
/// referrer host instead of the full referrer, the primary language subtag instead of the whole
/// <c>Accept-Language</c> list (specification §E.6.3).
/// </remarks>
public sealed record ClientContext
{
    /// <summary>Operating system platform.</summary>
    public required Platform Platform { get; init; }

    /// <summary>Form factor.</summary>
    public required DeviceClass DeviceClass { get; init; }

    /// <summary>Surface the request came from.</summary>
    public required ClientChannel Channel { get; init; }

    /// <summary>User agent family, for example <c>Chrome</c>.</summary>
    public string? UaFamily { get; init; }

    /// <summary>Operating system family, for example <c>iOS</c>.</summary>
    public string? OsFamily { get; init; }

    /// <summary>Operating system version, normalised through <c>VersionComparer.TryNormalize</c>.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Application version reported by the SDK, normalised the same way.</summary>
    public string? AppVersion { get; init; }

    /// <summary>ISO-3166-1 alpha-2 country code in upper case.</summary>
    public string? Country { get; init; }

    /// <summary>Sub country region from the GeoIP database.</summary>
    public string? Region { get; init; }

    /// <summary>Primary language subtag from <c>Accept-Language</c> in lower case, for example <c>sk</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Normalised host of the referring page. The full referrer is never kept.</summary>
    public string? ReferrerHost { get; init; }

    /// <summary>
    /// The client is a verified crawler. Crawlers get the Open Graph page and are excluded from
    /// campaign statistics (TC-106).
    /// </summary>
    public bool IsCrawler { get; init; }

    /// <summary>
    /// The user agent claims to be a crawler but reverse DNS does not confirm it (TC-107). Such a
    /// client is treated as an ordinary visitor, and the discrepancy is recorded.
    /// </summary>
    public bool IsSpoofedBot { get; init; }

    /// <summary>Name of the crawler when it was verified, for example <c>googlebot</c>.</summary>
    public string? CrawlerName { get; init; }

    /// <summary>Remote address. It is used for hashing and geo lookup and never stored raw.</summary>
    public System.Net.IPAddress? RemoteIp { get; init; }

    /// <summary>Query string parameters carried over from the request.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Instant the request was received, in UTC.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// A context that asserts nothing: everything unknown, no address, no query and
    /// <see cref="DateTimeOffset.MinValue"/> as the receive time. Used as the neutral element in
    /// rule simulation and in tests, never for a real request.
    /// </summary>
    public static ClientContext Empty { get; } = new()
    {
        Platform = Platform.Unknown,
        DeviceClass = DeviceClass.Unknown,
        Channel = ClientChannel.Unknown,
        ReceivedAt = DateTimeOffset.MinValue,
    };
}
