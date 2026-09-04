using System.Text;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Dle.Control.Workers;

/// <summary>
/// Leader election over a PostgreSQL session advisory lock (§B.3).
/// </summary>
/// <remarks>
/// <para>
/// §B.3 is explicit about the constraint: background services run inside the control plane process
/// and elect a leader "through a Postgres advisory lock — no further system". That sentence is a
/// product decision, not an implementation detail. A self-hosted deployment that already has to
/// run PostgreSQL must not also be told to run ZooKeeper, etcd, Redis Sentinel or a Kubernetes
/// lease controller in order to have a nightly job run once instead of three times.
/// </para>
/// <para>
/// The lock is a <em>session</em> advisory lock held on a dedicated connection for the duration of
/// one pass, and this is what makes it safe. It is not a row, so nothing has to expire; it is not
/// a lease, so no clock has to agree; and when a replica is killed mid-pass its connection dies
/// with it and PostgreSQL releases the lock immediately — no other replica has to wait out a
/// timeout to discover that the leader is gone. The cost is one idle connection per running pass,
/// which is the cheapest thing in this design.
/// </para>
/// <para>
/// <c>pg_try_advisory_lock</c> rather than <c>pg_advisory_lock</c>: a replica that does not get the
/// lock is not the leader for this tick and should go back to sleep, not queue up behind the leader
/// and run the same pass immediately afterwards.
/// </para>
/// </remarks>
public sealed partial class PostgresLeaderLock
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresLeaderLock> _logger;

    /// <summary>Creates the elector.</summary>
    /// <param name="dataSource">The primary connection pool.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PostgresLeaderLock(NpgsqlDataSource dataSource, ILogger<PostgresLeaderLock> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Derives the advisory lock key of a job name.
    /// </summary>
    /// <param name="jobName">Stable name of the job, for example <c>dle.worker.rollup</c>.</param>
    /// <returns>The 64 bit key.</returns>
    /// <remarks>
    /// FNV-1a over the UTF-8 name. Two different jobs colliding would mean one of them never runs
    /// while the other holds the lock, so the space is the full 64 bits rather than a small integer
    /// somebody has to keep a registry of.
    /// </remarks>
    public static long KeyOf(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;

        foreach (byte value in Encoding.UTF8.GetBytes(jobName))
        {
            hash ^= value;
            hash *= prime;
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// Tries to become the leader for one pass.
    /// </summary>
    /// <param name="jobName">Stable name of the job.</param>
    /// <param name="timeout">Upper bound on opening the connection and taking the lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A handle to dispose when the pass is over, or <see langword="null"/> when another replica
    /// holds the lock or the database could not be reached. Both are "not the leader"; neither is
    /// an error the caller has to handle differently (SHARED-KERNEL §17.9).
    /// </returns>
    public async Task<LeaderLease?> TryAcquireAsync(
        string jobName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        long key = KeyOf(jobName);

        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        NpgsqlConnection? connection = null;

        try
        {
            connection = await _dataSource.OpenConnectionAsync(bounded.Token);

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            _ = command.Parameters.AddWithValue("key", key);

            object? acquired = await command.ExecuteScalarAsync(bounded.Token);

            if (acquired is not true)
            {
                await connection.DisposeAsync();
                return null;
            }

            LeaderLease lease = new(connection, key, jobName);
            connection = null;

            return lease;
        }
        catch (NpgsqlException exception)
        {
            // Not the leader. A database that cannot be reached is exactly the situation in which
            // running the job anyway would be wrong, so the failure is counted and the pass skipped.
            LogLockUnavailable(_logger, jobName, exception);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogLockTimedOut(_logger, jobName);
            return null;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Warning,
        Message = "The leader lock for {JobName} could not be taken; this pass is skipped.")]
    private static partial void LogLockUnavailable(ILogger logger, string jobName, Exception exception);

    [LoggerMessage(
        EventId = 5602,
        Level = LogLevel.Warning,
        Message = "Taking the leader lock for {JobName} timed out; this pass is skipped.")]
    private static partial void LogLockTimedOut(ILogger logger, string jobName);
}

/// <summary>
/// Leadership over one job, for as long as this object lives.
/// </summary>
/// <remarks>
/// Disposal releases the lock and returns the connection to the pool. It is deliberately explicit
/// rather than relying on the connection closing: an unlocked-then-reused pooled connection is
/// cheaper than a discarded one, and the explicit unlock is what lets the next tick of the same
/// replica take the lock again without waiting for a pool cycle.
/// </remarks>
public sealed class LeaderLease : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private bool _released;

    /// <summary>Creates the lease.</summary>
    /// <param name="connection">The connection holding the session lock.</param>
    /// <param name="key">The advisory lock key.</param>
    /// <param name="jobName">Name of the job this lease covers.</param>
    internal LeaderLease(NpgsqlConnection connection, long key, string jobName)
    {
        _connection = connection;
        _key = key;
        JobName = jobName;
    }

    /// <summary>Name of the job this lease covers.</summary>
    public string JobName { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;

        try
        {
            await using NpgsqlCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            _ = command.Parameters.AddWithValue("key", _key);

            _ = await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (NpgsqlException)
        {
            // The connection is already broken, which means PostgreSQL has released the lock on its
            // own. Nothing to do and nothing to report: the desired state has been reached by
            // another route. Disposing the connection below is still required.
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
