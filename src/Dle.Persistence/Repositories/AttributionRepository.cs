using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes installations and the attributions that tie them to clicks (§B.5.3).
/// </summary>
public sealed class AttributionRepository
{
    /// <summary>PostgreSQL SQLSTATE for a unique violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>Name of the index that enforces one attribution per installation.</summary>
    private const string InstallUniqueIndex = "uq_attributions_install";

    /// <summary>Name of the partial index that enforces one installation per click (TC-144).</summary>
    private const string ClickUniqueIndex = "uq_attributions_click";

    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used to stamp installations and matches.</param>
    public AttributionRepository(DleDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the installation row for an SDK install identifier, creating it on first sight.
    /// </summary>
    /// <param name="candidate">The installation as reported by the SDK.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored installation.</returns>
    /// <remarks>
    /// The unique index on <c>(app_id, install_id)</c> is what makes a repeated resolve idempotent,
    /// and the race between two concurrent first opens is resolved by catching its violation rather
    /// than by locking: the loser simply reads the row the winner wrote (TC-143, FR-188).
    /// </remarks>
    public async Task<Install> GetOrCreateInstallAsync(
        Install candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        Install? existing = await _db.Installs
            .FirstOrDefaultAsync(
                i => i.AppId == candidate.AppId && i.InstallId == candidate.InstallId,
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        if (candidate.FirstOpenAt == default)
        {
            candidate.FirstOpenAt = _timeProvider.GetUtcNow();
        }

        _db.Installs.Add(candidate);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return candidate;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, indexName: null))
        {
            _db.Entry(candidate).State = EntityState.Detached;

            return await _db.Installs
                .FirstAsync(
                    i => i.AppId == candidate.AppId && i.InstallId == candidate.InstallId,
                    cancellationToken);
        }
    }

    /// <summary>Reads the attribution of an installation.</summary>
    /// <param name="installRowId">Primary key of the installation row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attribution, or <see langword="null"/>.</returns>
    public async Task<AttributionRecord?> FindByInstallAsync(
        Guid installRowId,
        CancellationToken cancellationToken)
    {
        return await _db.Attributions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.InstallId == installRowId, cancellationToken);
    }

    /// <summary>Reads the attribution that claimed a click.</summary>
    /// <param name="clickId">The public click identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attribution, or <see langword="null"/> when the click is unclaimed.</returns>
    public async Task<AttributionRecord?> FindByClickIdAsync(
        string clickId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clickId);

        return await _db.Attributions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ClickId == clickId, cancellationToken);
    }

    /// <summary>
    /// Writes an attribution, honouring both uniqueness rules of §B.5.3.
    /// </summary>
    /// <param name="record">The attribution to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and the attribution now in force.</returns>
    /// <remarks>
    /// <para>
    /// Two invariants hold here and neither is left to application discipline. One installation has
    /// at most one attribution, and one click is credited to at most one installation (TC-144).
    /// Both are unique indexes; this method checks first so the ordinary case gets a clear answer
    /// rather than an exception, and catches the violation so the racing case gets the same answer
    /// instead of a 500.
    /// </para>
    /// <para>
    /// A foreign key from <c>attributions.click_id</c> to <c>click_events</c> is impossible — the
    /// click stream is partitioned with a composite primary key, so there is nothing unique on
    /// <c>click_id</c> alone to reference — which is exactly why the partial unique index and this
    /// transaction exist.
    /// </para>
    /// <para>
    /// The whole thing runs inside the retrying execution strategy, because with
    /// <c>EnableRetryOnFailure</c> a user initiated transaction has to be replayable as a unit.
    /// </para>
    /// </remarks>
    public async Task<AttributionWriteResult> TryCreateAsync(
        AttributionRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.MatchedAt == default)
        {
            record.MatchedAt = _timeProvider.GetUtcNow();
        }

        IExecutionStrategy strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            token => WriteAsync(record, token),
            cancellationToken);
    }

    /// <summary>Performs one attempt of <see cref="TryCreateAsync"/> inside its own transaction.</summary>
    /// <param name="record">The attribution to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and the attribution now in force.</returns>
    private async Task<AttributionWriteResult> WriteAsync(
        AttributionRecord record,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken);

        AttributionRecord? forInstall = await _db.Attributions
            .FirstOrDefaultAsync(a => a.InstallId == record.InstallId, cancellationToken);

        if (forInstall is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AttributionWriteResult(AttributionOutcome.AlreadyAttributed, forInstall);
        }

        if (record.ClickId is { Length: > 0 } clickId)
        {
            bool claimed = await _db.Attributions
                .AnyAsync(a => a.ClickId == clickId, cancellationToken);

            if (claimed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AttributionWriteResult(AttributionOutcome.ClickAlreadyClaimed, Record: null);
            }
        }

        _db.Attributions.Add(record);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AttributionWriteResult(AttributionOutcome.Created, record);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, ClickUniqueIndex))
        {
            // Another installation claimed the same click between the check and the insert. The
            // index is what actually decided it; this branch only translates the decision.
            _db.Entry(record).State = EntityState.Detached;
            await transaction.RollbackAsync(cancellationToken);
            return new AttributionWriteResult(AttributionOutcome.ClickAlreadyClaimed, Record: null);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, InstallUniqueIndex))
        {
            _db.Entry(record).State = EntityState.Detached;
            await transaction.RollbackAsync(cancellationToken);

            AttributionRecord existing = await _db.Attributions
                .AsNoTracking()
                .FirstAsync(a => a.InstallId == record.InstallId, cancellationToken);

            return new AttributionWriteResult(AttributionOutcome.AlreadyAttributed, existing);
        }
    }

    /// <summary>Tells a unique index violation from any other write failure.</summary>
    /// <param name="exception">The failure reported by EF Core.</param>
    /// <param name="indexName">The index that must have been violated, or
    /// <see langword="null"/> to accept any.</param>
    /// <returns><see langword="true"/> when the failure is that unique violation.</returns>
    private static bool IsUniqueViolation(DbUpdateException exception, string? indexName)
    {
        if (exception.InnerException is not PostgresException postgres
            || !string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal))
        {
            return false;
        }

        return indexName is null
            || string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
    }
}
