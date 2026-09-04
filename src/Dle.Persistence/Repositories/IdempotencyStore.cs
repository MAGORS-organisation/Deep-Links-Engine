using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Stores and replays the responses of idempotent requests (§B.7.3).
/// </summary>
/// <remarks>
/// A client that retries after a timeout must not create a second link. The key alone cannot decide
/// that, though: the same key with a different body is a client bug, and replaying the first
/// response would hide it. The request hash is therefore stored and compared, and a mismatch is a
/// conflict rather than a replay.
/// </remarks>
public sealed class IdempotencyStore
{
    /// <summary>Response status of a reservation whose request has not finished yet.</summary>
    private const int InProgressStatus = 0;

    /// <summary>PostgreSQL SQLSTATE for a unique violation.</summary>
    private const string UniqueViolation = "23505";

    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<DlePersistenceOptions> _options;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used for expiry.</param>
    /// <param name="options">Persistence options, for the retention window.</param>
    public IdempotencyStore(
        DleDbContext db,
        TimeProvider timeProvider,
        IOptions<DlePersistenceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>
    /// Reserves a key for a request, or reports what to do with a repeat.
    /// </summary>
    /// <param name="key">The <c>Idempotency-Key</c> header value.</param>
    /// <param name="endpoint">Route the key is used against, so the same key on another route
    /// conflicts instead of replaying the wrong response.</param>
    /// <param name="requestHash">Hash of the canonical request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdict, with the stored response when replaying.</returns>
    /// <remarks>
    /// The reservation is an insert, so two concurrent retries race on the primary key rather than
    /// on a read-then-write: the loser catches the unique violation and re-reads, and gets the same
    /// answer the winner will produce.
    /// </remarks>
    public async Task<IdempotencyLookup> BeginAsync(
        string key,
        string endpoint,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(requestHash);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        IdempotencyRecord? existing = await FindAsync(key, endpoint, cancellationToken);

        if (existing is not null)
        {
            return Evaluate(existing, requestHash, now);
        }

        IdempotencyRecord reservation = new()
        {
            Key = key,
            Endpoint = endpoint,
            RequestHash = requestHash,
            ResponseStatus = InProgressStatus,
            ResponseBody = null,
            CreatedAt = now,
            ExpiresAt = now.AddHours(_options.Value.IdempotencyRetentionHours),
        };

        _db.IdempotencyRecords.Add(reservation);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdempotencyLookup.Proceed;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _db.Entry(reservation).State = EntityState.Detached;

            IdempotencyRecord? winner = await FindAsync(key, endpoint, cancellationToken);
            return winner is null
                ? IdempotencyLookup.Proceed
                : Evaluate(winner, requestHash, now);
        }
    }

    /// <summary>Stores the response of a request that held a reservation.</summary>
    /// <param name="key">The <c>Idempotency-Key</c> header value.</param>
    /// <param name="endpoint">Route the key was used against.</param>
    /// <param name="responseStatus">HTTP status to replay.</param>
    /// <param name="responseBody">Body to replay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> CompleteAsync(
        string key,
        string endpoint,
        int responseStatus,
        string? responseBody,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(responseStatus, 100);

        return await _db.IdempotencyRecords
            .Where(r => r.Key == key && r.Endpoint == endpoint)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.ResponseStatus, responseStatus)
                    .SetProperty(r => r.ResponseBody, responseBody),
                cancellationToken);
    }

    /// <summary>
    /// Releases a reservation whose request failed, so the caller may retry immediately.
    /// </summary>
    /// <param name="key">The <c>Idempotency-Key</c> header value.</param>
    /// <param name="endpoint">Route the key was used against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed.</returns>
    /// <remarks>
    /// Only an unfinished reservation is released. A stored response stays, because replaying it is
    /// the entire point; deleting it on the way out of an error path would turn a completed request
    /// into a repeatable one.
    /// </remarks>
    public async Task<int> AbandonAsync(
        string key,
        string endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        return await _db.IdempotencyRecords
            .Where(r => r.Key == key
                && r.Endpoint == endpoint
                && r.ResponseStatus == InProgressStatus)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Deletes records whose retention window has passed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed.</returns>
    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the pruning job removes expired records of every tenant");

        DateTimeOffset now = _timeProvider.GetUtcNow();

        return await _db.IdempotencyRecords
            .AcrossTenants()
            .Where(r => r.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Loads the record for a key within the tenant in scope.</summary>
    /// <param name="key">The key.</param>
    /// <param name="endpoint">The route.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    private async Task<IdempotencyRecord?> FindAsync(
        string key,
        string endpoint,
        CancellationToken cancellationToken)
    {
        return await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key && r.Endpoint == endpoint, cancellationToken);
    }

    /// <summary>Decides what a repeat of a known key means.</summary>
    /// <param name="record">The stored record.</param>
    /// <param name="requestHash">Hash of the body now being presented.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The verdict.</returns>
    private static IdempotencyLookup Evaluate(
        IdempotencyRecord record,
        byte[] requestHash,
        DateTimeOffset now)
    {
        if (record.ExpiresAt <= now)
        {
            // Past its retention window the key means nothing, and the request is a new one.
            return IdempotencyLookup.Proceed;
        }

        // The hash is not a secret, but comparing it in constant time costs nothing and keeps the
        // habit intact: a length-leaking or early-exit comparison of a digest is exactly the shape
        // shared kernel §17.6 forbids elsewhere.
        if (!CryptographicOperations.FixedTimeEquals(record.RequestHash, requestHash))
        {
            return IdempotencyLookup.Conflict;
        }

        return record.ResponseStatus == InProgressStatus
            ? IdempotencyLookup.InProgress
            : new IdempotencyLookup(
                IdempotencyOutcome.Replay,
                record.ResponseStatus,
                record.ResponseBody);
    }

    /// <summary>Tells a unique violation from any other write failure.</summary>
    /// <param name="exception">The failure reported by EF Core.</param>
    /// <returns><see langword="true"/> when the failure is a unique violation.</returns>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal);
}
