namespace Dle.Domain.Analytics;

/// <summary>
/// Kind of event reported by a mobile or web SDK through <c>POST /v1/events</c> (§B.7.2).
/// </summary>
public enum SdkEventType
{
    /// <summary>An app or universal link opened the application (§B.6.4).</summary>
    LinkOpen = 0,

    /// <summary>The first launch after installation.</summary>
    FirstOpen = 1,

    /// <summary>A user session started.</summary>
    Session = 2,

    /// <summary>A monetizable conversion, optionally carrying a value and currency.</summary>
    Conversion = 3,

    /// <summary>An application specific event identified by its name.</summary>
    Custom = 4,
}
