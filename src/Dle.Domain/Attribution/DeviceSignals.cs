namespace Dle.Domain.Attribution;

/// <summary>
/// Device signals used by the probabilistic matcher (§A.2.5). They are processed only when
/// <c>ConsentDecision.AllowProbabilisticMatch</c> is set.
/// </summary>
/// <remarks>
/// <para>
/// The signals are deliberately coarse; none of them is a strong device fingerprint. When the
/// effective consent mode is anything other than <c>full</c> they are dropped before they reach
/// storage — not written and deleted later (TC-145, TC-146).
/// </para>
/// <para>
/// Every property is optional. A signal that is missing on either side of a comparison
/// contributes nothing and its weight is not redistributed, so an install reporting few signals
/// can never reach the confidence of one reporting all of them.
/// </para>
/// </remarks>
public sealed record DeviceSignals
{
    /// <summary>Reported locale, for example <c>sk-SK</c>. Only the primary subtag is compared.</summary>
    public string? Language { get; init; }

    /// <summary>Screen geometry in pixels, written as width by height, for example <c>1080x2400</c>.</summary>
    public string? Screen { get; init; }

    /// <summary>Offset of the device time zone from UTC in minutes, for example <c>120</c>.</summary>
    public int? TimezoneOffsetMinutes { get; init; }

    /// <summary>Operating system version, compared with the version semantics of the shared kernel.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Device model reported by the SDK. Recorded as evidence, never scored.</summary>
    public string? DeviceModel { get; init; }

    /// <summary>
    /// Truncated network prefix of the remote address (/24 for IPv4, /48 for IPv6). The server
    /// fills this in from the connection; a client supplied value is never trusted.
    /// </summary>
    public string? IpPrefix { get; init; }
}
