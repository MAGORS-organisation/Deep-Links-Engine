namespace Dle.Domain.Analytics;

/// <summary>
/// One row of the partitioned click stream (specification §B.5.3).
/// The event is produced on the hot path and written asynchronously through a bounded
/// channel, so it must stay allocation friendly and free of behaviour.
/// </summary>
/// <remarks>
/// Privacy: the raw IP address is never stored. Depending on the effective consent mode the
/// event carries a salted HMAC of the address (<see cref="IpHash"/>) and, only with full
/// consent, the truncated network prefix (<see cref="IpPrefix"/>).
/// </remarks>
public sealed record ClickEvent
{
    /// <summary>Event identifier, a time ordered UUIDv7.</summary>
    public required Guid Id { get; init; }

    /// <summary>Instant the click was received, in UTC. Also the partitioning key.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Owning tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Identifier of the link that was resolved.</summary>
    public required long LinkId { get; init; }

    /// <summary>Public click identifier handed to the client and carried in the store referrer.</summary>
    public required string ClickId { get; init; }

    /// <summary>HMAC of the remote address with the daily rotated salt, or <see langword="null"/>.</summary>
    public byte[]? IpHash { get; init; }

    /// <summary>Truncated network prefix (/24 for IPv4, /48 for IPv6), full consent only.</summary>
    public string? IpPrefix { get; init; }

    /// <summary>User agent family reported by the classifier, for example <c>Chrome</c>.</summary>
    public string? UaFamily { get; init; }

    /// <summary>Operating system family, for example <c>iOS</c>.</summary>
    public string? OsFamily { get; init; }

    /// <summary>Normalized operating system version, for example <c>17.4.1</c>.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Device class, lowercase: <c>phone</c>, <c>tablet</c>, <c>desktop</c>, <c>bot</c> or <c>unknown</c>.</summary>
    public string? DeviceClass { get; init; }

    /// <summary>ISO-3166-1 alpha-2 country code in upper case.</summary>
    public string? Country { get; init; }

    /// <summary>Sub-country region as reported by the GeoIP database.</summary>
    public string? Region { get; init; }

    /// <summary>Primary language subtag in lower case, for example <c>sk</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Normalized host of the referring page. The full referrer is never stored.</summary>
    public string? ReferrerHost { get; init; }

    /// <summary>Client channel name, one of the constants on <c>ChannelNames</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>Decision name, one of the constants on <see cref="DecisionNames"/>.</summary>
    public required string Decision { get; init; }

    /// <summary>Deterministic A/B bucket in the range 0..99, when an A/B rule matched.</summary>
    public short? AbBucket { get; init; }

    /// <summary>Effective consent mode: <c>off</c>, <c>aggregate_only</c> or <c>full</c>.</summary>
    public required string ConsentMode { get; init; }

    /// <summary>Whether the client was classified as a crawler or other automated agent.</summary>
    public required bool IsBot { get; init; }

    /// <summary>Whether the user agent claimed to be a known bot but reverse DNS did not confirm it.</summary>
    public bool SpoofedBot { get; init; }

    /// <summary>Server side processing latency in milliseconds.</summary>
    public short? LatencyMs { get; init; }

    /// <summary>Additional low cardinality attributes stored as JSON.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}
