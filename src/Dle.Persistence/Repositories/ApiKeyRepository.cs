namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes control-plane API keys (FR-242).
/// </summary>
/// <remarks>
/// Authentication happens before a tenant is in scope — the key is what establishes it — so the
/// lookup by prefix is one of the few genuinely cross-tenant reads in the control plane. It is
/// written as such, explicitly, rather than by leaving the tenant filter off everything.
/// </remarks>
public sealed class ApiKeyRepository
{
    private readonly DleDbContext _db;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    public ApiKeyRepository(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Finds the key with a given public prefix, for authentication.
    /// </summary>
    /// <param name="prefix">The public prefix presented by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The key, or <see langword="null"/> when there is none or it has been revoked.</returns>
    /// <remarks>
    /// The returned row carries the Argon2id hash, which the caller compares with
    /// <c>CryptographicOperations.FixedTimeEquals</c> and never with equality (shared kernel §17.6).
    /// A revoked key is filtered out here rather than checked afterwards, so forgetting the check
    /// is not possible; expiry is left to the caller because an expired key deserves a different
    /// answer from an unknown one.
    /// </remarks>
    public async Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "an API key is what establishes the tenant, so it cannot be looked up within one");

        return await _db.ApiKeys
            .AsNoTracking()
            .AcrossTenants()
            .FirstOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);
    }

    /// <summary>Lists the keys of the tenant in scope.</summary>
    /// <param name="includeRevoked">Whether revoked keys are listed too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The keys, newest first.</returns>
    public async Task<IReadOnlyList<ApiKey>> ListAsync(
        bool includeRevoked,
        CancellationToken cancellationToken)
    {
        IQueryable<ApiKey> query = _db.ApiKeys.AsNoTracking();

        if (includeRevoked)
        {
            query = query.IncludeSoftDeleted();
        }

        return await query
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Issues a key for the tenant in scope.</summary>
    /// <param name="key">The key row, carrying the prefix and the hash of the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored key.</returns>
    public async Task<ApiKey> AddAsync(ApiKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync(cancellationToken);
        return key;
    }

    /// <summary>Revokes a key of the tenant in scope.</summary>
    /// <param name="id">The key identifier.</param>
    /// <param name="revokedAt">Instant of revocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    /// <remarks>
    /// The row is kept so that the audit entries referring to it keep referring to something that
    /// exists; the soft delete filter is what stops it authenticating anything again.
    /// </remarks>
    public async Task<bool> RevokeAsync(
        Guid id,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
        {
            return false;
        }

        key.RevokedAt = revokedAt;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Records that a key was used, without loading it.
    /// </summary>
    /// <param name="id">The key identifier.</param>
    /// <param name="usedAt">Instant of use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    /// <remarks>
    /// This runs on every authenticated request, so it is an <c>ExecuteUpdate</c> rather than a load
    /// and a save: one statement, no materialization, no change tracking. The query filters still
    /// apply — <c>ExecuteUpdate</c> composes over the filtered queryable — so the tenant scoping is
    /// not lost by stepping around <c>SaveChanges</c>.
    /// </remarks>
    public async Task<int> TouchLastUsedAsync(
        Guid id,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the key is touched during authentication, before the tenant it identifies is in scope");

        return await _db.ApiKeys
            .AcrossTenants()
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(k => k.LastUsedAt, usedAt),
                cancellationToken);
    }
}
