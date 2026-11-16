using Dle.Domain.Ports;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Erases an installation's attributable records within a single tenant for GDPR Article 17 / DSAR
/// compliance (§E.6.3).
/// </summary>
public sealed class PrivacyErasureStore : IPrivacyErasureStore
{
    private readonly DleDbContext _db;

    /// <summary>Creates the erasure store.</summary>
    /// <param name="db">The control-plane database context.</param>
    public PrivacyErasureStore(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    /// <inheritdoc />
    public async Task<InstallErasureResult> EraseInstallAsync(
        Guid tenantId,
        string installId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));
        }

        if (!_db.Database.IsRelational())
        {
            return await ExecuteCoreAsync(tenantId, installId, useTransaction: false, cancellationToken);
        }

        IExecutionStrategy strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            token => ExecuteCoreAsync(tenantId, installId, useTransaction: true, token),
            cancellationToken);
    }

    private async Task<InstallErasureResult> ExecuteCoreAsync(
        Guid tenantId,
        string installId,
        bool useTransaction,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = useTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            List<Guid> installRowIds = await _db.Installs
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.InstallId == installId)
                .Select(i => i.Id)
                .ToListAsync(cancellationToken);

            int erasedAttributions = 0;
            int erasedInstalls = 0;

            if (installRowIds.Count > 0)
            {
                erasedAttributions = await _db.Attributions
                    .Where(a => a.TenantId == tenantId && installRowIds.Contains(a.InstallId))
                    .ExecuteDeleteAsync(cancellationToken);

                erasedInstalls = await _db.Installs
                    .Where(i => i.TenantId == tenantId && installRowIds.Contains(i.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            int erasedSdkEvents = _db.Database.IsRelational()
                ? await _db.Database.ExecuteSqlAsync(
                    $"DELETE FROM sdk_events WHERE tenant_id = {tenantId} AND install_id = {installId}",
                    cancellationToken)
                : 0;

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new InstallErasureResult(erasedInstalls, erasedAttributions, erasedSdkEvents);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
