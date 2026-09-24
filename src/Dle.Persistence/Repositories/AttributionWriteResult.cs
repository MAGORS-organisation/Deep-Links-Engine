namespace Dle.Persistence.Repositories;

/// <summary>
/// The result of offering an attribution to the store.
/// </summary>
/// <param name="Outcome">Whether the attribution was written, already existed, or was refused.</param>
/// <param name="Record">
/// The attribution now in force for the installation, or <see langword="null"/> when the click was
/// already claimed and the installation therefore has none.
/// </param>
public sealed record AttributionWriteResult(AttributionOutcome Outcome, AttributionRecord? Record);
