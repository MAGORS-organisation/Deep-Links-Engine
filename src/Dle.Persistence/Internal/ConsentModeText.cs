using Dle.Domain.Privacy;

namespace Dle.Persistence.Internal;

/// <summary>
/// Converts between the stored text of a consent mode and <see cref="ConsentMode"/>.
/// </summary>
/// <remarks>
/// Consent modes are stored as text rather than as an integer, so that the value is legible in the
/// database and survives a reordering of the enum (shared kernel §12). Parsing is total and fails
/// closed: an unrecognised value becomes <see cref="ConsentMode.Off"/>, because a row whose consent
/// state cannot be read is not a row to collect against.
/// </remarks>
internal static class ConsentModeText
{
    /// <summary>Stored text for <see cref="ConsentMode.Off"/>.</summary>
    internal const string Off = "off";

    /// <summary>Stored text for <see cref="ConsentMode.AggregateOnly"/>.</summary>
    internal const string AggregateOnly = "aggregate_only";

    /// <summary>Stored text for <see cref="ConsentMode.Full"/>.</summary>
    internal const string Full = "full";

    /// <summary>Renders a consent mode as the text stored in the database.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The stored text.</returns>
    internal static string From(ConsentMode mode) => mode switch
    {
        ConsentMode.Full => Full,
        ConsentMode.AggregateOnly => AggregateOnly,
        _ => Off,
    };

    /// <summary>Reads a stored consent mode, defaulting to <see cref="ConsentMode.Off"/>.</summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The parsed mode.</returns>
    internal static ConsentMode Parse(string? text) => text switch
    {
        Full => ConsentMode.Full,
        AggregateOnly => ConsentMode.AggregateOnly,
        _ => ConsentMode.Off,
    };

    /// <summary>Reads an optional stored consent mode.</summary>
    /// <param name="text">The stored text, or <see langword="null"/> when no override is set.</param>
    /// <returns>The parsed mode, or <see langword="null"/> when there is no override.</returns>
    internal static ConsentMode? ParseOptional(string? text) =>
        string.IsNullOrEmpty(text) ? null : Parse(text);
}
