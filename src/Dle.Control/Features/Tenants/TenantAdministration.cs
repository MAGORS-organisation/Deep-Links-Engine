using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Tenants;

/// <summary>
/// Reads and writes tenants other than the one in scope (FR-241).
/// </summary>
/// <remarks>
/// <para>
/// Tenant administration is the one control-plane operation that legitimately crosses tenants:
/// provisioning happens before the tenant exists, and an instance operator edits tenants that are
/// not the one their own credential established. Every method here therefore opens a
/// <see cref="CrossTenantScope"/> with a written reason and drops the tenant filter by name — the
/// sanctioned form of that step, and the only one that is greppable.
/// </para>
/// <para>
/// Who may call this is settled before it: <c>DlePolicies.TenantsWrite</c> requires the caller to be
/// recognised as the instance operator, either because their tenant is named in
/// <c>Dle:Control:InstanceTenantId</c> or because <c>Dle:Control:AllowTenantSelfService</c> is on.
/// With neither set the policy denies every caller, which is the right posture for a deployment that
/// has not decided (SHARED-KERNEL §17.9).
/// </para>
/// </remarks>
public sealed class TenantAdministration
{
    private readonly DleDbContext _db;

    /// <summary>Creates the service.</summary>
    /// <param name="db">The control-plane context.</param>
    public TenantAdministration(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Loads any tenant by identifier.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="includeDeleted">Whether a deactivated tenant is returned too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/>.</returns>
    public async Task<Tenant?> GetAsync(
        Guid id,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "instance administration reads a tenant that is not the caller's own");

        IQueryable<Tenant> query = _db.Tenants.AsNoTracking().AcrossTenants();

        if (includeDeleted)
        {
            query = query.IncludeSoftDeleted();
        }

        return await query.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    /// <summary>Changes the name, the consent mode or the status of a tenant.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="name">The new display name, or <see langword="null"/> to keep it.</param>
    /// <param name="consentMode">The new consent mode, or <see langword="null"/> to keep it.</param>
    /// <param name="status">The new lifecycle status, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated tenant, or <see langword="null"/> when there is no such tenant.</returns>
    /// <remarks>
    /// The slug is deliberately absent. It appears in operator-facing URLs and in the audit trail,
    /// and a trail whose subject can be renamed underneath it stops being evidence (FR-246).
    /// </remarks>
    public async Task<Tenant?> UpdateAsync(
        Guid id,
        string? name,
        string? consentMode,
        string? status,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "instance administration edits a tenant that is not the caller's own");

        Tenant? tenant = await _db.Tenants
            .AcrossTenants()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        if (name is not null)
        {
            tenant.Name = name;
        }

        if (consentMode is not null)
        {
            tenant.ConsentMode = consentMode;
        }

        if (status is not null)
        {
            tenant.Status = status;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return tenant;
    }

    /// <summary>Reports whether a slug is already taken.</summary>
    /// <param name="slug">The candidate slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a tenant already uses it.</returns>
    /// <remarks>
    /// Deactivated tenants are included. Their audit entries still refer to the slug, and handing it
    /// to somebody else would make an old entry read as though it described the new tenant.
    /// </remarks>
    public async Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "a tenant slug is unique across the instance, so the check has to span tenants");

        return await _db.Tenants
            .AsNoTracking()
            .AcrossTenants()
            .IncludeSoftDeleted()
            .AnyAsync(t => t.Slug == slug, cancellationToken);
    }
}
