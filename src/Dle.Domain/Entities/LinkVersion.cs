namespace Dle.Domain.Entities;

/// <summary>
/// One historical revision of a link (FR-107). Maps to <c>link_versions</c>.
/// </summary>
/// <remarks>
/// The whole link is stored as a snapshot rather than as a field level diff. A link is small, and
/// a snapshot answers the question that is actually asked after an incident — what exactly was
/// this link serving at that moment — without having to replay a chain of diffs.
/// </remarks>
public class LinkVersion
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>The link this revision belongs to.</summary>
    public long LinkId { get; set; }

    /// <summary>Revision number, matching <see cref="Link.Version"/> at the time of the change.</summary>
    public int Version { get; set; }

    /// <summary>The complete link as it was, stored as a JSON document.</summary>
    public string Snapshot { get; set; } = "{}";

    /// <summary>Operator who made the change.</summary>
    public Guid? ChangedBy { get; set; }

    /// <summary>Instant of the change, in UTC.</summary>
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Optional note describing why the change was made.</summary>
    public string? ChangeNote { get; set; }
}
