using Dle.Domain.Privacy;
using Dle.Persistence.Internal;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes tenants (FR-241).
/// </summary>
/// <remarks>
/// Tenants are the one table where the tenant filter has to be stepped over regularly: provisioning
/// a tenant happens before there is a tenant, and authentication has to find one from a slug before
/// it can establish one. Every such method opens a <see cref="CrossTenantScope"/> with a stated
/// reason and drops only the tenant filter by name, so the step over is visible in the code rather
/// than implied by an <c>IgnoreQueryFilters()</c> somewhere.
/// </remarks>
public sealed class TenantRepository
{
    private readonly DleDbContext _db;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    public TenantRepository(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Loads the tenant in scope.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/> when it does not exist, is deleted, or
    /// belongs to somebody else — the three are deliberately indistinguishable (TC-166).</returns>
    public async Task<Tenant?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    /// <summary>Finds a tenant by its slug, before authentication has established one.</summary>
    /// <param name="slug">The tenant slug, matched case insensitively by the citext column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/>.</returns>
    public async Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "resolving a tenant from its slug during sign in, before a tenant is in scope");

        return await _db.Tenants
            .AsNoTracking()
            .AcrossTenants()
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
    }

    /// <summary>Lists tenants for the instance operator.</summary>
    /// <param name="includeDeleted">Whether to include tenants marked deleted.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenants, oldest first, which for UUIDv7 keys is creation order.</returns>
    public async Task<IReadOnlyList<Tenant>> ListAsync(
        bool includeDeleted,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "instance administration lists every tenant");

        IQueryable<Tenant> query = _db.Tenants.AsNoTracking().AcrossTenants();

        if (includeDeleted)
        {
            query = query.IncludeSoftDeleted();
        }

        return await query
            .OrderBy(t => t.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Provisions a tenant.</summary>
    /// <param name="tenant">The tenant to create. Its identifier may be left empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created tenant, with its identifier and creation instant filled in.</returns>
    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "provisioning a tenant happens before that tenant can be in scope");

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    /// <summary>Changes the consent mode of the tenant in scope (§E.6.2, FR-248).</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="mode">The new mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public async Task<bool> SetConsentModeAsync(
        Guid id,
        ConsentMode mode,
        CancellationToken cancellationToken)
    {
        Tenant? tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        tenant.ConsentMode = ConsentModeText.From(mode);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Marks a tenant deleted without removing its rows.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    /// <remarks>
    /// A hard delete would cascade through links, keys and the audit trail, and the audit trail is
    /// the one thing that has to outlive the objects it describes. The row stays; the soft delete
    /// filter stops it appearing anywhere it should not.
    /// </remarks>
    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "instance administration deactivates a tenant");

        Tenant? tenant = await _db.Tenants
            .AcrossTenants()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        tenant.Status = "deleted";
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
