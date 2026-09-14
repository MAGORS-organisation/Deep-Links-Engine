using Dle.Domain.Primitives;
using Dle.Domain.WellKnown;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes registered applications and their pairing with domains (FR-141, FR-142).
/// </summary>
public sealed class AppRepository
{
    /// <summary>Stored platform value for an iOS application.</summary>
    public const string IosPlatform = "ios";

    /// <summary>Stored platform value for an Android application.</summary>
    public const string AndroidPlatform = "android";

    private readonly DleDbContext _db;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    public AppRepository(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Loads one application of the tenant in scope.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application, or <see langword="null"/>.</returns>
    public async Task<App?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <summary>Lists the applications of the tenant in scope.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applications, grouped by platform then bundle identifier.</returns>
    public async Task<IReadOnlyList<App>> ListAsync(CancellationToken cancellationToken)
    {
        return await _db.Apps
            .AsNoTracking()
            .OrderBy(a => a.Platform)
            .ThenBy(a => a.BundleId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Registers an application for the tenant in scope.</summary>
    /// <param name="app">The application to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored application.</returns>
    public async Task<App> AddAsync(App app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);

        _db.Apps.Add(app);
        await _db.SaveChangesAsync(cancellationToken);
        return app;
    }

    /// <summary>Saves changes made to a tracked application.</summary>
    /// <param name="app">The application to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> UpdateAsync(App app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);

        _db.Apps.Update(app);
        return await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes an application of the tenant in scope.</summary>
    /// <param name="id">The application identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (app is null)
        {
            return false;
        }

        _db.Apps.Remove(app);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Pairs an application with a domain, so the host advertises it.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="domainId">The domain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the pairing was created, <see langword="false"/> when
    /// it already existed.</returns>
    /// <exception cref="InvalidOperationException">Either side belongs to another tenant.</exception>
    /// <remarks>
    /// <c>app_domains</c> carries no tenant column, so both ends are loaded through the tenant
    /// filter first. That check is not decoration: it is the only thing standing between a caller
    /// with a foreign identifier and a row that would make one tenant's host advertise another
    /// tenant's application.
    /// </remarks>
    public async Task<bool> AttachDomainAsync(
        Guid appId,
        Guid domainId,
        CancellationToken cancellationToken)
    {
        bool appIsOurs = await _db.Apps.AnyAsync(a => a.Id == appId, cancellationToken);
        bool domainIsOurs = await _db.Domains.AnyAsync(d => d.Id == domainId, cancellationToken);

        if (!appIsOurs || !domainIsOurs)
        {
            throw new InvalidOperationException(
                "The application and the domain must both belong to the tenant in scope.");
        }

        bool exists = await _db.AppDomains
            .AnyAsync(ad => ad.AppId == appId && ad.DomainId == domainId, cancellationToken);

        if (exists)
        {
            return false;
        }

        _db.AppDomains.Add(new AppDomainEntity { AppId = appId, DomainId = domainId });
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Removes the pairing of an application and a domain.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="domainId">The domain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a pairing was removed.</returns>
    public async Task<bool> DetachDomainAsync(
        Guid appId,
        Guid domainId,
        CancellationToken cancellationToken)
    {
        bool appIsOurs = await _db.Apps.AnyAsync(a => a.Id == appId, cancellationToken);
        if (!appIsOurs)
        {
            return false;
        }

        AppDomainEntity? pairing = await _db.AppDomains
            .FirstOrDefaultAsync(ad => ad.AppId == appId && ad.DomainId == domainId, cancellationToken);

        if (pairing is null)
        {
            return false;
        }

        _db.AppDomains.Remove(pairing);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Lists the applications a host advertises, for one platform.</summary>
    /// <param name="host">The host, normalized before the lookup.</param>
    /// <param name="platform">Stored platform value, <c>ios</c> or <c>android</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applications paired with that host.</returns>
    public async Task<IReadOnlyList<App>> ListForHostAsync(
        string host,
        string platform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        if (!HostNormalizer.TryNormalize(host, out string normalized))
        {
            return [];
        }

        // Association files are served for a host, and a host belongs to exactly one tenant, so the
        // lookup itself is what establishes which tenant is involved.
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "association files are built per host, before a tenant is in scope");

        IQueryable<App> query =
            from app in _db.Apps.AsNoTracking().AcrossTenants()
            join pairing in _db.AppDomains.AsNoTracking() on app.Id equals pairing.AppId
            join domain in _db.Domains.AsNoTracking().AcrossTenants() on pairing.DomainId equals domain.Id
            where app.Platform == platform && domain.Host == normalized
            orderby app.BundleId
            select app;

        return await query.ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the Apple App Site Association entries for a host (FR-141).
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries, empty when the host advertises no iOS application.</returns>
    /// <remarks>
    /// An application with no team identifier is skipped rather than rendered with a placeholder:
    /// an association file listing a malformed application identifier is worse than one that omits
    /// it, because iOS rejects the whole file rather than the bad entry (TC-122).
    /// </remarks>
    public async Task<IReadOnlyList<AasaAppEntry>> GetAasaEntriesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<App> apps = await ListForHostAsync(host, IosPlatform, cancellationToken);
        List<AasaAppEntry> entries = new(apps.Count);

        foreach (App app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.TeamId) || string.IsNullOrWhiteSpace(app.BundleId))
            {
                continue;
            }

            entries.Add(new AasaAppEntry
            {
                AppId = string.Create(CultureInfo.InvariantCulture, $"{app.TeamId}.{app.BundleId}"),
                AppClipAppId = string.IsNullOrWhiteSpace(app.AppClipBundleId)
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"{app.TeamId}.{app.AppClipBundleId}"),
                Components = WellKnownBuilder.DefaultComponents,
            });
        }

        return entries;
    }

    /// <summary>
    /// Builds the Digital Asset Links entries for a host (FR-142).
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries, empty when the host advertises no Android application.</returns>
    /// <remarks>
    /// Both fingerprint lists are published. The Play App Signing fingerprint is the one that
    /// matters for an application distributed through the Play Store, but a build installed from a
    /// developer machine is signed with the upload certificate, and dropping either list breaks one
    /// of the two (FR-144).
    /// </remarks>
    public async Task<IReadOnlyList<AndroidAppEntry>> GetAssetLinkEntriesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<App> apps = await ListForHostAsync(host, AndroidPlatform, cancellationToken);
        List<AndroidAppEntry> entries = new(apps.Count);

        foreach (App app in apps)
        {
            List<string> fingerprints = new(
                app.PlaySigningFingerprints.Count + app.CertFingerprints.Count);
            fingerprints.AddRange(app.PlaySigningFingerprints);

            foreach (string fingerprint in app.CertFingerprints)
            {
                if (!fingerprints.Contains(fingerprint, StringComparer.OrdinalIgnoreCase))
                {
                    fingerprints.Add(fingerprint);
                }
            }

            if (string.IsNullOrWhiteSpace(app.BundleId) || fingerprints.Count == 0)
            {
                continue;
            }

            entries.Add(new AndroidAppEntry
            {
                PackageName = app.BundleId,
                Sha256CertFingerprints = fingerprints,
                DynamicComponents = WellKnownBuilder.DefaultComponents,
            });
        }

        return entries;
    }
}
