namespace Dle.Control.Features.Attribution;

/// <summary>
/// Keys the engine reads out of <c>click_events.extra</c> during attribution.
/// </summary>
/// <remarks>
/// <para>
/// <c>click_events</c> has fixed columns for the coarse dimensions every report needs — country,
/// operating system family, language, device class — and a <c>jsonb</c> column for the rest.
/// Attribution reads three values out of that column, and naming them here rather than inline is
/// what makes them a contract between the edge that writes them and this module that reads them.
/// </para>
/// <para>
/// All three are written by the edge only when the consent gate allowed it. Their absence is
/// therefore normal and never an error: a missing signal simply contributes nothing to a score.
/// </para>
/// </remarks>
public static class ClickExtraKeys
{
    /// <summary>Screen geometry recorded with the click, for example <c>1080x2400</c>.</summary>
    public const string Screen = "screen";

    /// <summary>UTC offset in minutes recorded with the click.</summary>
    public const string TimezoneOffsetMinutes = "tz_offset";

    /// <summary>Deep link path the routing engine resolved for the click.</summary>
    public const string DeeplinkPath = "deeplink_path";
}
