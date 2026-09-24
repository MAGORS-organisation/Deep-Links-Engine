using System.Text.Json;

namespace Dle.Persistence.Internal;

/// <summary>
/// Builds the history row that accompanies every change to a link, from whichever repository makes
/// the change.
/// </summary>
/// <remarks>
/// A link's <c>version</c> is also its optimistic-concurrency token, so every write that changes
/// the row bumps it, and a history that skipped those bumps would have holes exactly where an
/// incident investigation looks. Editing, quarantining and releasing therefore all go through
/// here: one snapshot per version, taken after the change was applied to the instance.
/// </remarks>
internal static class LinkRevisions
{
    /// <summary>Creates the history row for the link's current version.</summary>
    /// <param name="link">The link, already carrying the new values and the new version.</param>
    /// <param name="changedBy">Who made the change, when a person did.</param>
    /// <param name="changeNote">Why it was made.</param>
    /// <param name="changedAt">When it was made.</param>
    /// <returns>The row to add to the context.</returns>
    internal static LinkVersion Create(Link link, Guid? changedBy, string? changeNote, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new LinkVersion
        {
            LinkId = link.Id,
            Version = link.Version,
            Snapshot = JsonSerializer.Serialize(link, DlePersistenceJsonContext.Default.Link),
            ChangedBy = changedBy,
            ChangedAt = changedAt,
            ChangeNote = changeNote,
        };
    }
}
