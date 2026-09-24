using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Apps;

/// <summary>
/// Reads and writes which hosts advertise which applications (FR-141, FR-142).
/// </summary>
/// <remarks>
/// <para>
/// <c>app_domains</c> carries no tenant column — §B.5.2 does not give it one — so every query here
/// reaches it only through the tenant-filtered <c>apps</c> and <c>domains</c> tables. That join is
/// not decoration: it is the only thing standing between a caller with a foreign identifier and a
/// row that would make one tenant's host advertise another tenant's application (T-09).
/// </para>
/// <para>
/// The context is used directly rather than through a repository because
/// <see cref="AppRepository"/> already owns the guarded single-pair operations, and what is missing
/// is only the set-shaped reads this feature needs.
/// </para>
/// </remarks>
public sealed class AppDomainPairings
{
    private readonly DleDbContext _db;
    private readonly AppRepository _apps;

    /// <summary>Creates the reader.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="apps">Application storage, which owns the guarded pair operations.</param>
    public AppDomainPairings(DleDbContext db, AppRepository apps)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(apps);

        _db = db;
        _apps = apps;
    }

    /// <summary>Reads the domains one application is paired with.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domain identifiers, empty when the application is not the tenant's.</returns>
    public async Task<IReadOnlyList<Guid>> ForAppAsync(Guid appId, CancellationToken cancellationToken)
    {
        return await (
            from pairing in _db.AppDomains.AsNoTracking()
            join app in _db.Apps.AsNoTracking() on pairing.AppId equals app.Id
            join domain in _db.Domains.AsNoTracking() on pairing.DomainId equals domain.Id
            where pairing.AppId == appId
            select pairing.DomainId).ToListAsync(cancellationToken);
    }

    /// <summary>Reads the pairings of every application of the tenant.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Application identifier to domain identifiers.</returns>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ForTenantAsync(
        CancellationToken cancellationToken)
    {
        var rows = await (
            from pairing in _db.AppDomains.AsNoTracking()
            join app in _db.Apps.AsNoTracking() on pairing.AppId equals app.Id
            join domain in _db.Domains.AsNoTracking() on pairing.DomainId equals domain.Id
            select new { pairing.AppId, pairing.DomainId }).ToListAsync(cancellationToken);

        Dictionary<Guid, IReadOnlyList<Guid>> pairings = [];

        foreach (var row in rows)
        {
            if (pairings.TryGetValue(row.AppId, out IReadOnlyList<Guid>? existing))
            {
                ((List<Guid>)existing).Add(row.DomainId);
            }
            else
            {
                pairings[row.AppId] = new List<Guid> { row.DomainId };
            }
        }

        return pairings;
    }

    /// <summary>Resolves domain identifiers to their hosts.</summary>
    /// <param name="domainIds">The domains.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hosts of the domains that belong to the tenant.</returns>
    public async Task<IReadOnlyList<string>> HostsAsync(
        IReadOnlyList<Guid> domainIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainIds);

        if (domainIds.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. domainIds];

        return await _db.Domains
            .AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => d.Host)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Makes the pairings of an application exactly the requested set.
    /// </summary>
    /// <param name="appId">The application.</param>
    /// <param name="desired">The domains it should be paired with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pairings that now exist.</returns>
    /// <remarks>
    /// A foreign domain identifier is not an error the caller is told about: the pair operation
    /// refuses it, and the identifier simply does not appear in the returned set. Reporting "that
    /// domain is not yours" would confirm it exists, which is the disclosure TC-166 forbids.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> ReplaceAsync(
        Guid appId,
        IReadOnlyList<Guid> desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);

        IReadOnlyList<Guid> current = await ForAppAsync(appId, cancellationToken);
        HashSet<Guid> wanted = [.. desired];
        HashSet<Guid> have = [.. current];

        foreach (Guid domainId in wanted)
        {
            if (have.Contains(domainId))
            {
                continue;
            }

            try
            {
                await _apps.AttachDomainAsync(appId, domainId, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Either end belongs to another tenant. The pairing is not created and the
                // identifier is left out of the result; nothing is disclosed about why.
                continue;
            }
        }

        foreach (Guid domainId in have)
        {
            if (!wanted.Contains(domainId))
            {
                await _apps.DetachDomainAsync(appId, domainId, cancellationToken);
            }
        }

        return await ForAppAsync(appId, cancellationToken);
    }
}
