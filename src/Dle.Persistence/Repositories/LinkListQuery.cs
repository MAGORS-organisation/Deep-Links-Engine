namespace Dle.Persistence.Repositories;

/// <summary>
/// Filter and page for <see cref="LinkRepository.ListAsync"/> (FR-109).
/// </summary>
/// <remarks>
/// Paging is keyset, not offset. A link list is ordered newest first and is written to constantly,
/// so an offset page would skip and repeat rows as the table shifts under it, and the cost of
/// <c>OFFSET n</c> grows with <c>n</c>. The cursor names the last row seen instead.
/// </remarks>
public sealed record LinkListQuery
{
    /// <summary>Restrict to one domain of the tenant.</summary>
    public Guid? DomainId { get; init; }

    /// <summary>Restrict to one campaign.</summary>
    public Guid? CampaignId { get; init; }

    /// <summary>Case insensitive fragment matched against the slug and the title.</summary>
    public string? Search { get; init; }

    /// <summary>Only links carrying every one of these tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Only links that are switched on.</summary>
    public bool? IsActive { get; init; }

    /// <summary>
    /// Include links withdrawn by abuse handling. They are hidden by the soft delete filter, which
    /// this drops by name — the tenant filter stays on either way (TC-103).
    /// </summary>
    public bool IncludeQuarantined { get; init; }

    /// <summary>Maximum number of rows to return.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Opaque cursor from the previous page, or <see langword="null"/> for the first.</summary>
    public string? Cursor { get; init; }
}
