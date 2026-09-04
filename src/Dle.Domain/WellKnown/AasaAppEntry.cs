namespace Dle.Domain.WellKnown;

/// <summary>
/// One iOS application registered on a link domain, as it will appear in
/// <c>apple-app-site-association</c> (FR-141).
/// </summary>
public sealed record AasaAppEntry
{
    /// <summary>
    /// The app ID: the ten-character team identifier, a dot, and the bundle identifier —
    /// for example <c>ABCDE12345.sk.zakaznik.app</c>.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>App ID of the associated App Clip, emitted in the <c>appclips</c> section. Optional.</summary>
    public string? AppClipAppId { get; init; }

    /// <summary>
    /// Path, fragment and query patterns for this application. When empty,
    /// <see cref="WellKnownBuilder.DefaultComponents"/> is used.
    /// </summary>
    public IReadOnlyList<AasaComponent> Components { get; init; } = [];
}
