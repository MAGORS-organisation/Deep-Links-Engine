using Dle.Domain.Ports;
using Dle.Domain.Primitives;
using Dle.Persistence.Internal;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes link domains and their verification history (FR-143, FR-145).
/// </summary>
public sealed class DomainRepository
{
    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used to stamp verification runs.</param>
    public DomainRepository(DleDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>Loads one domain of the tenant in scope.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domain, or <see langword="null"/>.</returns>
    public async Task<LinkDomain?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _db.Domains
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    /// <summary>Lists the domains of the tenant in scope.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domains, ordered by host.</returns>
    public async Task<IReadOnlyList<LinkDomain>> ListAsync(CancellationToken cancellationToken)
    {
        return await _db.Domains
            .AsNoTracking()
            .OrderBy(d => d.Host)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Finds a domain of the tenant in scope by host.</summary>
    /// <param name="host">The host, normalized before the lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domain, or <see langword="null"/>.</returns>
    public async Task<LinkDomain?> FindByHostAsync(string host, CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(host, out string normalized))
        {
            return null;
        }

        return await _db.Domains
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Host == normalized, cancellationToken);
    }

    /// <summary>Registers a domain for the tenant in scope.</summary>
    /// <param name="domain">The domain to add. Its host is normalized before it is stored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored domain.</returns>
    /// <exception cref="ArgumentException">The host cannot be normalized.</exception>
    public async Task<LinkDomain> AddAsync(LinkDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (!HostNormalizer.TryNormalize(domain.Host, out string normalized))
        {
            throw new ArgumentException("The host is not a usable domain name.", nameof(domain));
        }

        domain.Host = normalized;
        _db.Domains.Add(domain);
        await _db.SaveChangesAsync(cancellationToken);
        return domain;
    }

    /// <summary>Saves changes made to a tracked domain.</summary>
    /// <param name="domain">The domain to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> UpdateAsync(LinkDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        _db.Domains.Update(domain);
        return await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes a domain of the tenant in scope.</summary>
    /// <param name="id">The domain identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was removed.</returns>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        LinkDomain? domain = await _db.Domains.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (domain is null)
        {
            return false;
        }

        _db.Domains.Remove(domain);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Appends a verification run and updates the summary columns of the domain (FR-143).
    /// </summary>
    /// <param name="verification">The run to record. Its checked instant is filled in when unset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded run.</returns>
    /// <exception cref="InvalidOperationException">The domain does not belong to the tenant in scope.</exception>
    /// <remarks>
    /// The history row and the status column are written in one save. Keeping only the column would
    /// answer "is it broken now" but not "since when", which is the question an intermittent
    /// association file failure actually raises.
    /// </remarks>
    public async Task<DomainVerification> RecordVerificationAsync(
        DomainVerification verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        // Load through the tenant filter first: domain_verifications carries no tenant column, so
        // this is the check that keeps a run from being written against a foreign domain.
        LinkDomain? domain = await _db.Domains
            .FirstOrDefaultAsync(d => d.Id == verification.DomainId, cancellationToken);

        if (domain is null)
        {
            throw new InvalidOperationException(
                "The domain does not exist or does not belong to the tenant in scope.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (verification.CheckedAt == default)
        {
            verification.CheckedAt = now;
        }

        _db.DomainVerifications.Add(verification);

        switch (verification.Kind)
        {
            case "aasa":
                domain.AasaStatus = verification.Status;
                break;
            case "assetlinks":
                domain.AssetlinksStatus = verification.Status;
                break;
            case "tls":
                domain.TlsStatus = verification.Status;
                break;
            default:
                break;
        }

        domain.LastVerifiedAt = verification.CheckedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return verification;
    }

    /// <summary>Reads the recent verification runs of a domain, newest first.</summary>
    /// <param name="domainId">The domain identifier.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The runs, or an empty list when the domain is not the tenant's.</returns>
    public async Task<IReadOnlyList<DomainVerification>> GetVerificationHistoryAsync(
        Guid domainId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The join through Domains is what applies the tenant filter to a table that has no tenant
        // column of its own.
        return await _db.DomainVerifications
            .AsNoTracking()
            .Where(v => v.DomainId == domainId
                && _db.Domains.Any(d => d.Id == v.DomainId))
            .OrderByDescending(v => v.CheckedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the runtime view of a host used by the edge and by the interstitial renderer.
    /// </summary>
    /// <param name="host">The host, normalized before the lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configuration, or <see langword="null"/> when the host is unknown.</returns>
    /// <remarks>
    /// This is one of the places where the repository returns a shared-kernel type rather than an
    /// entity: <see cref="DomainRuntimeConfig"/> carries the tenant's consent mode alongside the
    /// domain's override, because the effective mode is the narrower of the two and neither row
    /// alone can answer it.
    /// </remarks>
    public async Task<DomainRuntimeConfig?> GetRuntimeConfigAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (!HostNormalizer.TryNormalize(host, out string normalized))
        {
            return null;
        }

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "a host maps to exactly one tenant, and the lookup is what discovers which");

        var row = await _db.Domains
            .AsNoTracking()
            .AcrossTenants()
            .Where(d => d.Host == normalized)
            .Join(
                _db.Tenants.AsNoTracking().AcrossTenants(),
                d => d.TenantId,
                t => t.Id,
                (d, t) => new
                {
                    d.Id,
                    d.TenantId,
                    d.Host,
                    d.ConsentModeOverride,
                    d.Branding,
                    d.DefaultOg,
                    d.IsActive,
                    TenantConsentMode = t.ConsentMode,
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new DomainRuntimeConfig
        {
            Id = row.Id,
            TenantId = row.TenantId,
            Host = row.Host,
            TenantConsentMode = ConsentModeText.Parse(row.TenantConsentMode),
            DomainConsentMode = ConsentModeText.ParseOptional(row.ConsentModeOverride),
            InterstitialBrandJson = row.Branding,
            DefaultLanguage = null,
            DefaultOg = JsonColumn.ReadOgMeta(row.DefaultOg),
            IsActive = row.IsActive,
        };
    }
}
