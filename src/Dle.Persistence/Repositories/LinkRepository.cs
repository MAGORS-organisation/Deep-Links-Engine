using System.Text.Json;

using Dle.Domain.Contracts;
using Dle.Domain.Links;
using Dle.Domain.Primitives;
using Dle.Persistence.Internal;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes links, their revision history and the snapshot the edge caches (FR-101, FR-107).
/// </summary>
public sealed class LinkRepository
{
    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used to stamp revisions.</param>
    public LinkRepository(DleDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>Loads one link of the tenant in scope.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="includeQuarantined">Whether a withdrawn link is returned too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link, or <see langword="null"/>.</returns>
    public async Task<Link?> GetAsync(
        long id,
        bool includeQuarantined,
        CancellationToken cancellationToken)
    {
        IQueryable<Link> query = _db.Links.AsNoTracking();

        if (includeQuarantined)
        {
            query = query.IncludeSoftDeleted();
        }

        return await query.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    /// <summary>Finds a link of the tenant in scope by domain and slug.</summary>
    /// <param name="domainId">The domain.</param>
    /// <param name="slug">The slug, normalized before the lookup.</param>
    /// <param name="includeQuarantined">Whether a withdrawn link is returned too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link, or <see langword="null"/>.</returns>
    public async Task<Link?> FindBySlugAsync(
        Guid domainId,
        string slug,
        bool includeQuarantined,
        CancellationToken cancellationToken)
    {
        if (!SlugPolicy.TryNormalize(slug, out string normalized))
        {
            return null;
        }

        IQueryable<Link> query = _db.Links.AsNoTracking();

        if (includeQuarantined)
        {
            query = query.IncludeSoftDeleted();
        }

        return await query.FirstOrDefaultAsync(
            l => l.DomainId == domainId && l.Slug == normalized,
            cancellationToken);
    }

    /// <summary>
    /// Reports whether a slug is already taken on a domain, so a create can answer
    /// <see cref="ProblemCodes.SlugTaken"/> instead of failing on the unique index.
    /// </summary>
    /// <param name="domainId">The domain.</param>
    /// <param name="slug">The slug, normalized before the lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the slug exists on that domain.</returns>
    /// <remarks>
    /// The check deliberately includes quarantined links: a withdrawn slug is not free for reuse,
    /// because the old short URL is still in circulation and handing it to somebody else would turn
    /// a 410 into a redirect to unrelated content.
    /// </remarks>
    public async Task<bool> SlugExistsAsync(
        Guid domainId,
        string slug,
        CancellationToken cancellationToken)
    {
        if (!SlugPolicy.TryNormalize(slug, out string normalized))
        {
            return false;
        }

        return await _db.Links
            .IncludeSoftDeleted()
            .AnyAsync(l => l.DomainId == domainId && l.Slug == normalized, cancellationToken);
    }

    /// <summary>Lists links of the tenant in scope, newest first, with keyset paging.</summary>
    /// <param name="query">Filter and page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page and the cursor for the next one.</returns>
    public async Task<PagedResponse<Link>> ListAsync(
        LinkListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);

        IQueryable<Link> links = _db.Links.AsNoTracking();

        if (query.IncludeQuarantined)
        {
            links = links.IncludeSoftDeleted();
        }

        if (query.DomainId is Guid domainId)
        {
            links = links.Where(l => l.DomainId == domainId);
        }

        if (query.CampaignId is Guid campaignId)
        {
            links = links.Where(l => l.CampaignId == campaignId);
        }

        if (query.IsActive is bool isActive)
        {
            links = links.Where(l => l.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = string.Create(CultureInfo.InvariantCulture, $"%{query.Search.Trim()}%");
            links = links.Where(l =>
                EF.Functions.ILike(l.Slug, pattern)
                || (l.Title != null && EF.Functions.ILike(l.Title, pattern)));
        }

        if (query.Tags is { Count: > 0 } tags)
        {
            // Array containment, which is what the GIN index on tags answers.
            List<string> required = [.. tags];
            links = links.Where(l => required.All(tag => l.Tags.Contains(tag)));
        }

        if (KeysetCursor.TryDecode(query.Cursor, out DateTimeOffset cursorCreatedAt, out long cursorId))
        {
            links = links.Where(l =>
                l.CreatedAt < cursorCreatedAt
                || (l.CreatedAt == cursorCreatedAt && l.Id < cursorId));
        }

        List<Link> page = await links
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        string? nextCursor = page.Count == query.Limit
            ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id)
            : null;

        return new PagedResponse<Link> { Items = page, NextCursor = nextCursor };
    }

    /// <summary>Creates a link and its first revision.</summary>
    /// <param name="link">The link. Its identifier must already be set (a Snowflake).</param>
    /// <param name="createdBy">Operator creating it, recorded on the revision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored link.</returns>
    /// <exception cref="ArgumentException">The identifier or the slug is missing or unusable.</exception>
    public async Task<Link> AddAsync(
        Link link,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.Id == 0)
        {
            throw new ArgumentException(
                "A link identifier is minted by the application before the insert, never by the " +
                "database.",
                nameof(link));
        }

        if (!SlugPolicy.TryNormalize(link.Slug, out string normalized))
        {
            throw new ArgumentException("The slug is not usable.", nameof(link));
        }

        link.Slug = normalized;
        link.Version = 1;

        _db.Links.Add(link);
        _db.LinkVersions.Add(CreateRevision(link, createdBy, changeNote: null));

        await _db.SaveChangesAsync(cancellationToken);
        return link;
    }

    /// <summary>
    /// Saves a change to a link and appends the revision that records it (FR-107).
    /// </summary>
    /// <param name="link">The link, already carrying the new values.</param>
    /// <param name="changedBy">Operator making the change.</param>
    /// <param name="changeNote">Why the change was made.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new version number.</returns>
    /// <remarks>
    /// The version increment and the history row are written in the same transaction as the change
    /// itself. A history that can lag behind the object it describes is not a history, and after an
    /// incident the question is always "what exactly was this link serving at that moment".
    /// </remarks>
    public async Task<int> UpdateAsync(
        Link link,
        Guid? changedBy,
        string? changeNote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!SlugPolicy.TryNormalize(link.Slug, out string normalized))
        {
            throw new ArgumentException("The slug is not usable.", nameof(link));
        }

        link.Slug = normalized;
        link.Version++;

        // Reads are untracked, so the instance handed in is usually a stranger to the change
        // tracker and Update attaches it. When the same unit of work also created or loaded this
        // link, a second instance with the same key cannot be attached; the values are copied onto
        // the tracked one instead, and it is the tracked instance the revision is taken from.
        Link tracked = _db.Links.Local.FirstOrDefault(candidate => candidate.Id == link.Id) ?? link;

        if (ReferenceEquals(tracked, link))
        {
            _db.Links.Update(link);
        }
        else
        {
            _db.Entry(tracked).CurrentValues.SetValues(link);
        }

        _db.LinkVersions.Add(CreateRevision(tracked, changedBy, changeNote));

        await _db.SaveChangesAsync(cancellationToken);
        return tracked.Version;
    }

    /// <summary>Deletes a link of the tenant in scope, with its revisions.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    /// <remarks>
    /// Deleting is for a link the tenant created by mistake. A link withdrawn for abuse is
    /// quarantined instead, never deleted, so that it keeps answering 410 with an explanation and
    /// the forensic trail survives (§E.3, TC-103).
    /// </remarks>
    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken)
    {
        Link? link = await _db.Links.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (link is null)
        {
            return false;
        }

        _db.Links.Remove(link);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Reads the revisions of a link, newest first.</summary>
    /// <param name="linkId">The link identifier.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revisions, or an empty list when the link is not the tenant's.</returns>
    public async Task<IReadOnlyList<LinkVersion>> GetRevisionsAsync(
        long linkId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // link_versions has no tenant column; the join through the tenant filtered link is what
        // scopes it.
        IQueryable<LinkVersion> query =
            from revision in _db.LinkVersions.AsNoTracking()
            join link in _db.Links.AsNoTracking().IncludeSoftDeleted()
                on revision.LinkId equals link.Id
            where revision.LinkId == linkId
            orderby revision.Version descending
            select revision;

        return await query.Take(limit).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the snapshot the edge caches for one short URL (§B.6.1).
    /// </summary>
    /// <param name="host">The host of the short URL.</param>
    /// <param name="slug">The slug of the short URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot, or <see langword="null"/> when host or slug is unknown.</returns>
    /// <remarks>
    /// <para>
    /// This is the control-plane twin of the Dapper query on the resolve path: the same shape, built
    /// for cache warming, previews and the rule simulator, where an extra hundred microseconds do
    /// not matter and the convenience does. The resolve path itself never calls it (ADR-004).
    /// </para>
    /// <para>
    /// Quarantined links are deliberately included. The edge has to find one in order to answer 410
    /// Gone rather than 404, and <see cref="LinkSnapshot.GetServeState"/> makes that decision from
    /// <see cref="LinkSnapshot.QuarantinedAt"/> — which it can only do if the row reaches it
    /// (TC-103).
    /// </para>
    /// </remarks>
    public async Task<LinkSnapshot?> GetSnapshotAsync(
        string host,
        string slug,
        CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(host, out string normalizedHost)
            || !SlugPolicy.TryNormalize(slug, out string normalizedSlug))
        {
            return null;
        }

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "a short URL identifies its tenant through the host, which is what this lookup resolves");

        var row = await (
            from link in _db.Links.AsNoTracking().AcrossTenants().IncludeSoftDeleted()
            join domain in _db.Domains.AsNoTracking().AcrossTenants() on link.DomainId equals domain.Id
            join tenant in _db.Tenants.AsNoTracking().AcrossTenants() on link.TenantId equals tenant.Id
            where domain.Host == normalizedHost && link.Slug == normalizedSlug
            select new
            {
                Link = link,
                DomainConsentOverride = domain.ConsentModeOverride,
                TenantConsentMode = tenant.ConsentMode,
            }).FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var apps = await (
            from app in _db.Apps.AsNoTracking().AcrossTenants()
            join pairing in _db.AppDomains.AsNoTracking() on app.Id equals pairing.AppId
            where pairing.DomainId == row.Link.DomainId
            select new { app.Platform, app.CustomScheme, app.StoreUrl }).ToListAsync(cancellationToken);

        var ios = apps.Find(a => a.Platform == AppRepository.IosPlatform);
        var android = apps.Find(a => a.Platform == AppRepository.AndroidPlatform);

        Link entity = row.Link;

        return new LinkSnapshot
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            DomainId = entity.DomainId,
            Slug = entity.Slug,
            TargetUrl = entity.TargetUrl,
            DeeplinkPath = entity.DeeplinkPath,
            RoutingRules = JsonColumn.ReadRoutingRules(entity.RoutingRules),
            Og = JsonColumn.ReadOgMeta(entity.OgMeta),
            Utm = JsonColumn.ReadStringMap(entity.Utm),
            Title = entity.Title,
            CampaignId = entity.CampaignId,
            IsActive = entity.IsActive,
            StartsAt = entity.StartsAt,
            ExpiresAt = entity.ExpiresAt,
            ExpiredUrl = entity.ExpiredUrl,
            QuarantinedAt = entity.QuarantinedAt,
            TenantConsentMode = ConsentModeText.Parse(row.TenantConsentMode),
            DomainConsentMode = ConsentModeText.ParseOptional(row.DomainConsentOverride),
            IosCustomScheme = ios?.CustomScheme,
            AndroidCustomScheme = android?.CustomScheme,
            IosStoreUrl = ios?.StoreUrl,
            AndroidStoreUrl = android?.StoreUrl,
        };
    }

    /// <summary>Builds the revision row that records the current state of a link.</summary>
    /// <param name="link">The link as it now is.</param>
    /// <param name="changedBy">Operator responsible for the change.</param>
    /// <param name="changeNote">Why the change was made.</param>
    /// <returns>The revision, ready to be added.</returns>
    /// <remarks>
    /// The whole link is stored rather than a field level diff. A link is small, and a snapshot
    /// answers the question actually asked after an incident without replaying a chain of diffs.
    /// The columns that already hold JSON are stored as the strings they are, so a revision round
    /// trips byte for byte instead of being reformatted by a second pass through a serializer.
    /// </remarks>
    private LinkVersion CreateRevision(Link link, Guid? changedBy, string? changeNote)
    {
        return new LinkVersion
        {
            LinkId = link.Id,
            Version = link.Version,
            Snapshot = JsonSerializer.Serialize(link, DlePersistenceJsonContext.Default.Link),
            ChangedBy = changedBy,
            ChangedAt = _timeProvider.GetUtcNow(),
            ChangeNote = changeNote,
        };
    }
}
