using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Identity;

/// <summary>
/// The candidate row behind a presented control-plane API key, together with the state of the tenant
/// it belongs to.
/// </summary>
/// <param name="KeyId">Identifier of the key row.</param>
/// <param name="TenantId">Tenant the key authenticates for.</param>
/// <param name="Role">Role granted by the key.</param>
/// <param name="Scopes">Scopes granted by the key; empty means the role alone governs.</param>
/// <param name="Hash">Argon2id hash of the key, in PHC form, as stored.</param>
/// <param name="ExpiresAt">Expiry instant, or <see langword="null"/> for a key that never expires.</param>
/// <param name="TenantStatus">Lifecycle status of the tenant.</param>
/// <param name="TenantCreatedAt">When the tenant was provisioned, for the §E.9 new-tenant limit.</param>
public sealed record ApiKeyCandidate(
    Guid KeyId,
    Guid TenantId,
    string Role,
    IReadOnlyList<string> Scopes,
    byte[] Hash,
    DateTimeOffset? ExpiresAt,
    string TenantStatus,
    DateTimeOffset TenantCreatedAt);

/// <summary>
/// The candidate row behind a presented SDK key.
/// </summary>
/// <param name="KeyId">Identifier of the key row.</param>
/// <param name="TenantId">Tenant the key authenticates for.</param>
/// <param name="AppId">Application the key is bound to.</param>
/// <param name="Hash">Argon2id hash of the key, in PHC form, as stored.</param>
/// <param name="TenantStatus">Lifecycle status of the tenant.</param>
/// <param name="TenantCreatedAt">When the tenant was provisioned.</param>
public sealed record SdkKeyCandidate(
    Guid KeyId,
    Guid TenantId,
    Guid AppId,
    byte[] Hash,
    string TenantStatus,
    DateTimeOffset TenantCreatedAt);

/// <summary>
/// Finds the row behind a presented key, before any tenant is in scope.
/// </summary>
/// <remarks>
/// <para>
/// Authentication is the one place in the control plane that genuinely cannot run inside a tenant:
/// the credential is what establishes which tenant this is. Every read here therefore opens a
/// <see cref="CrossTenantScope"/> with a written reason and drops the tenant filter by name, which
/// is the sanctioned form of that step (FR-241, T-09).
/// </para>
/// <para>
/// The soft-delete filter is deliberately left on. A revoked key and a deleted tenant are invisible
/// here, so neither can authenticate, and forgetting to check for them is not possible.
/// </para>
/// </remarks>
public sealed class DleCredentialStore
{
    private readonly DleDbContext _db;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The control-plane context.</param>
    public DleCredentialStore(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Finds the control-plane key with a given public prefix.</summary>
    /// <param name="prefix">The non-secret prefix read from the presented key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidate, or <see langword="null"/> when there is no live key with that prefix.</returns>
    public async Task<ApiKeyCandidate?> FindApiKeyAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "an API key is what establishes the tenant, so it cannot be looked up within one");

        var row = await (
            from key in _db.ApiKeys.AsNoTracking().AcrossTenants()
            join tenant in _db.Tenants.AsNoTracking().AcrossTenants() on key.TenantId equals tenant.Id
            where key.Prefix == prefix
            select new
            {
                key.Id,
                key.TenantId,
                key.Role,
                key.Scopes,
                key.Hash,
                key.ExpiresAt,
                TenantStatus = tenant.Status,
                TenantCreatedAt = tenant.CreatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new ApiKeyCandidate(
                row.Id,
                row.TenantId,
                row.Role,
                row.Scopes,
                row.Hash,
                row.ExpiresAt,
                row.TenantStatus,
                row.TenantCreatedAt);
    }

    /// <summary>Finds the SDK key with a given public prefix.</summary>
    /// <param name="prefix">The non-secret prefix read from the presented key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidate, or <see langword="null"/> when there is no active key with that prefix.</returns>
    public async Task<SdkKeyCandidate?> FindSdkKeyAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "an SDK key is what establishes the tenant, so it cannot be looked up within one");

        var row = await (
            from key in _db.SdkKeys.AsNoTracking().AcrossTenants()
            join tenant in _db.Tenants.AsNoTracking().AcrossTenants() on key.TenantId equals tenant.Id
            where key.KeyPrefix == prefix && key.IsActive
            select new
            {
                key.Id,
                key.TenantId,
                key.AppId,
                key.Hash,
                TenantStatus = tenant.Status,
                TenantCreatedAt = tenant.CreatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new SdkKeyCandidate(
                row.Id,
                row.TenantId,
                row.AppId,
                row.Hash,
                row.TenantStatus,
                row.TenantCreatedAt);
    }

    /// <summary>Records that a control-plane key was used.</summary>
    /// <param name="keyId">The key identifier.</param>
    /// <param name="usedAt">Instant of use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    /// <remarks>
    /// One statement, no materialisation, no change tracking: this runs on every authenticated
    /// request that is not served from the verification cache.
    /// </remarks>
    public async Task<int> TouchApiKeyAsync(
        Guid keyId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the key is touched during authentication, before the tenant it identifies is in scope");

        return await _db.ApiKeys
            .AcrossTenants()
            .Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(k => k.LastUsedAt, usedAt),
                cancellationToken);
    }
}
