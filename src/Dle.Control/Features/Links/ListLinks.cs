using System.Text.Json;

using Dle.Control.Configuration;
using Dle.Control.Infrastructure;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Links;

/// <summary>
/// <c>GET /api/v1/links</c> and <c>GET /api/v1/links/{id}</c> — search, tag filter and paging
/// (FR-109).
/// </summary>
/// <remarks>
/// Paging is by cursor rather than by page number. A link list is ordered newest first and is
/// written to constantly, so an offset page skips and repeats rows as the table shifts underneath
/// it, and the cost of <c>OFFSET n</c> grows with <c>n</c>. The cursor names the last row seen.
/// </remarks>
public static class ListLinks
{
    /// <summary>Lists the tenant's links.</summary>
    /// <param name="search">Case-insensitive fragment matched against the slug and the title.</param>
    /// <param name="tags">Comma separated tags; a link must carry every one of them.</param>
    /// <param name="domainId">Restrict to one domain.</param>
    /// <param name="campaignId">Restrict to one campaign or template.</param>
    /// <param name="isActive">Restrict to switched-on or switched-off links.</param>
    /// <param name="includeArchived">Include links withdrawn by abuse handling (TC-103).</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cursor">Cursor from the previous page.</param>
    /// <param name="links">Link storage.</param>
    /// <param name="domains">Domain storage, read once to resolve every host on the page.</param>
    /// <param name="presenter">Builds the short URL and the QR URL.</param>
    /// <param name="options">Control-plane options, for the page size bounds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page and the cursor for the next one.</returns>
    public static async Task<IResult> HandleAsync(
        string? search,
        string? tags,
        Guid? domainId,
        Guid? campaignId,
        bool? isActive,
        bool? includeArchived,
        int? limit,
        string? cursor,
        LinkRepository links,
        DomainRepository domains,
        LinkPresentation presenter,
        IOptions<DleControlOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(options);

        DleControlOptions control = options.Value;
        int pageSize = limit ?? control.DefaultPageSize;

        if (pageSize < 1 || pageSize > control.MaxPageSize)
        {
            return DleProblem.Validation(
                "limit",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The page size must be between 1 and {control.MaxPageSize}."));
        }

        LinkListQuery query = new()
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Tags = ParseTags(tags),
            DomainId = domainId,
            CampaignId = campaignId,
            IsActive = isActive,
            IncludeQuarantined = includeArchived ?? false,
            Limit = pageSize,
            Cursor = cursor,
        };

        PagedResponse<Link> page = await links.ListAsync(query, cancellationToken);

        // One read for every host on the page rather than one per row: a page of fifty links on one
        // domain would otherwise be fifty identical lookups.
        IReadOnlyList<LinkDomain> tenantDomains = await domains.ListAsync(cancellationToken);
        Dictionary<Guid, string> hosts = new(tenantDomains.Count);

        foreach (LinkDomain domain in tenantDomains)
        {
            hosts[domain.Id] = domain.Host;
        }

        List<LinkResponse> items = new(page.Items.Count);

        foreach (Link link in page.Items)
        {
            items.Add(presenter.Project(
                link,
                hosts.TryGetValue(link.DomainId, out string? host) ? host : string.Empty));
        }

        return TypedResults.Ok(new PagedResponse<LinkResponse>
        {
            Items = items,
            NextCursor = page.NextCursor,
        });
    }

    /// <summary>Reads one link.</summary>
    /// <param name="id">The link identifier, as text because identifiers are 64 bit.</param>
    /// <param name="links">Link storage.</param>
    /// <param name="domains">Domain storage.</param>
    /// <param name="presenter">Builds the short URL and the QR URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the link, or 404.</returns>
    /// <remarks>
    /// A link belonging to another tenant is not in the queryable at all, so it arrives here as an
    /// ordinary miss and leaves as 404 — never 403, which would confirm the identifier exists
    /// (TC-166, SHARED-KERNEL §17.7).
    /// </remarks>
    public static async Task<IResult> GetAsync(
        string id,
        LinkRepository links,
        DomainRepository domains,
        LinkPresentation presenter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(presenter);

        if (!LinkRoute.TryParseId(id, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        Link? link = await links.GetAsync(linkId, includeQuarantined: true, cancellationToken);

        if (link is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        LinkDomain? domain = await domains.GetAsync(link.DomainId, cancellationToken);

        return TypedResults.Ok(presenter.Project(link, domain?.Host ?? string.Empty));
    }

    /// <summary>Reads the revisions of a link, newest first (FR-107).</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="limit">How many revisions to read.</param>
    /// <param name="links">Link storage.</param>
    /// <param name="options">Control-plane options, for the cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revisions, or 404 when there is no such link.</returns>
    /// <remarks>
    /// The stored snapshot is returned as it was written rather than re-projected into the current
    /// response shape. A history that silently changes shape when the API does is not a history, and
    /// the question after an incident is what this link was serving at that moment — including the
    /// fields the current version no longer has.
    /// </remarks>
    public static async Task<IResult> GetVersionsAsync(
        string id,
        int? limit,
        LinkRepository links,
        IOptions<DleControlOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(options);

        if (!LinkRoute.TryParseId(id, out long linkId))
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        int cap = options.Value.MaxLinkVersions;
        int take = Math.Clamp(limit ?? cap, 1, cap);

        Link? link = await links.GetAsync(linkId, includeQuarantined: true, cancellationToken);

        if (link is null)
        {
            return DleProblem.NotFound("There is no link with that identifier.");
        }

        IReadOnlyList<LinkVersion> revisions =
            await links.GetRevisionsAsync(linkId, take, cancellationToken);

        List<LinkVersionSummary> items = new(revisions.Count);

        foreach (LinkVersion revision in revisions)
        {
            items.Add(new LinkVersionSummary
            {
                Version = revision.Version,
                ChangedBy = revision.ChangedBy,
                ChangedAt = revision.ChangedAt,
                ChangeNote = revision.ChangeNote,
                Snapshot = ParseSnapshot(revision.Snapshot),
            });
        }

        return TypedResults.Ok(new PagedResponse<LinkVersionSummary>
        {
            Items = items,
            Total = items.Count,
        });
    }

    /// <summary>
    /// Reads a stored revision document so that it can be embedded rather than escaped.
    /// </summary>
    /// <param name="snapshot">The stored document.</param>
    /// <returns>The document as a JSON value, or an empty object when it will not parse.</returns>
    /// <remarks>
    /// The element is cloned because the parsed document is disposed on the way out of this method,
    /// and an element that outlives its document reads freed memory.
    /// </remarks>
    private static JsonElement ParseSnapshot(string snapshot)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>Splits the comma separated tag filter (FR-109).</summary>
    /// <param name="tags">The query value.</param>
    /// <returns>The tags, or <see langword="null"/> when none were supplied.</returns>
    private static List<string>? ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        string[] parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return null;
        }

        List<string> normalized = new(parts.Length);

        foreach (string part in parts)
        {
            normalized.Add(part.ToLowerInvariant());
        }

        return normalized;
    }
}

/// <summary>
/// One historical revision of a link, as stored (FR-107).
/// </summary>
/// <remarks>
/// <see cref="Snapshot"/> is the raw document written at the time. It is passed through rather than
/// re-serialized so that a revision round trips byte for byte;
/// <see cref="Dle.Domain.Contracts.LinkVersionResponse"/> exists for callers that want the snapshot
/// projected into the current response shape, which this endpoint deliberately does not do.
/// </remarks>
public sealed record LinkVersionSummary
{
    /// <summary>Revision number.</summary>
    public required int Version { get; init; }

    /// <summary>Operator who made the change.</summary>
    public Guid? ChangedBy { get; init; }

    /// <summary>When the change was made.</summary>
    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>Note supplied with the change.</summary>
    public string? ChangeNote { get; init; }

    /// <summary>The stored link document, exactly as it was written.</summary>
    public required JsonElement Snapshot { get; init; }
}
