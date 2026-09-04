using Dle.Domain.Analytics;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Wire names of <see cref="SdkEventType"/>, as an SDK spells them on <c>POST /v1/events</c>
/// (§B.7.2).
/// </summary>
/// <remarks>
/// <para>
/// The names are part of the public contract, so they live in one place rather than in a
/// <c>switch</c> inside a handler. <c>link_open</c> is the one that matters most and is the one
/// most often forgotten: when the operating system opens the application through a verified
/// Universal or App Link, no HTTP request reaches the engine at all, so this event is the only
/// evidence the open ever happened (§B.6.4, FR-223).
/// </para>
/// <para>
/// Parsing fails closed. An unrecognised type is not silently recorded as <c>custom</c>, because
/// that would turn a typo in an integration into a permanent stream of events nobody can report
/// on; it is counted as rejected and the batch's other events are still accepted.
/// </para>
/// </remarks>
public static class SdkEventNames
{
    /// <summary>The application was opened through one of our links (§B.6.4, FR-223).</summary>
    public const string LinkOpen = "link_open";

    /// <summary>First launch after an installation.</summary>
    public const string FirstOpen = "first_open";

    /// <summary>A foreground session started.</summary>
    public const string Session = "session";

    /// <summary>A conversion, optionally carrying a monetary value.</summary>
    public const string Conversion = "conversion";

    /// <summary>Anything the host application defines for itself.</summary>
    public const string Custom = "custom";

    /// <summary>Every accepted event type name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        LinkOpen,
        FirstOpen,
        Session,
        Conversion,
        Custom,
    ];

    /// <summary>Maps an event type onto its wire name.</summary>
    /// <param name="type">The event type.</param>
    /// <returns>The wire name.</returns>
    public static string From(SdkEventType type) => type switch
    {
        SdkEventType.LinkOpen => LinkOpen,
        SdkEventType.FirstOpen => FirstOpen,
        SdkEventType.Session => Session,
        SdkEventType.Conversion => Conversion,
        _ => Custom,
    };

    /// <summary>
    /// Parses a wire name.
    /// </summary>
    /// <param name="value">The name as received. Untrusted; case and surrounding whitespace are
    /// ignored.</param>
    /// <param name="type">The parsed type when the name is recognised.</param>
    /// <returns><see langword="true"/> when the name is one of <see cref="All"/>.</returns>
    public static bool TryParse(string? value, out SdkEventType type)
    {
        type = SdkEventType.Custom;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, LinkOpen, StringComparison.OrdinalIgnoreCase))
        {
            type = SdkEventType.LinkOpen;
            return true;
        }

        if (string.Equals(trimmed, FirstOpen, StringComparison.OrdinalIgnoreCase))
        {
            type = SdkEventType.FirstOpen;
            return true;
        }

        if (string.Equals(trimmed, Session, StringComparison.OrdinalIgnoreCase))
        {
            type = SdkEventType.Session;
            return true;
        }

        if (string.Equals(trimmed, Conversion, StringComparison.OrdinalIgnoreCase))
        {
            type = SdkEventType.Conversion;
            return true;
        }

        if (string.Equals(trimmed, Custom, StringComparison.OrdinalIgnoreCase))
        {
            type = SdkEventType.Custom;
            return true;
        }

        return false;
    }
}
