using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Hands out slug counter values from a block claimed in advance (ADR-007).
/// </summary>
/// <remarks>
/// <para>
/// A slug is a 47 bit counter value pushed through a keyed Feistel permutation. Because the
/// permutation is a bijection, every counter value produces exactly one slug and a collision is
/// structurally impossible — creating a link never needs a retry loop, and never needs to ask the
/// database whether a slug is free. What remains is handing out counter values, and doing that one
/// at a time would put a round trip on every link creation.
/// </para>
/// <para>
/// This class claims a range once and then mints from memory. Values left unused when a process
/// restarts are simply skipped, which is harmless: the space holds 1.4 × 10^14 values, and a
/// deployment burning a thousand of them per restart would need to restart a hundred billion times
/// to notice. What must never happen is reuse, so a block is never returned once claimed and the
/// claim itself is serialised by an advisory lock.
/// </para>
/// <para>
/// Registered as a singleton, because the in-memory block is the whole point; it opens its own
/// service scope for the rare refill.
/// </para>
/// </remarks>
public sealed class SlugSequenceAllocator : IDisposable
{
    /// <summary>Highest counter value that still yields an eight character base62 slug.</summary>
    public const long MaxSequenceValue = (1L << SlugSequence.MaxBits) - 1;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DlePersistenceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _next;
    private long _blockEnd = -1;
    private bool _disposed;

    /// <summary>Creates the allocator.</summary>
    /// <param name="scopeFactory">Used to open a scope for the occasional refill.</param>
    /// <param name="options">Persistence options, for the block size and the sequence name.</param>
    /// <param name="timeProvider">Clock used to stamp the claim.</param>
    public SlugSequenceAllocator(
        IServiceScopeFactory scopeFactory,
        IOptions<DlePersistenceOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Takes the next counter value, claiming a new block when the current one runs out.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A counter value never handed out before.</returns>
    /// <exception cref="InvalidOperationException">The 47 bit space is exhausted.</exception>
    /// <remarks>
    /// The whole method is serialised by a semaphore rather than by interlocked arithmetic with a
    /// separate refill path. Link creation is a control-plane operation measured in thousands a
    /// day, so the contention is nil, and a lock free counter that has to coordinate with a refill
    /// is precisely the kind of cleverness that hands out a duplicate once a year.
    /// </remarks>
    public async Task<long> NextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_next > _blockEnd)
            {
                (long start, long end) = await ClaimBlockAsync(cancellationToken);
                _next = start;
                _blockEnd = end;
            }

            return _next++;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// Derives the advisory lock key for a sequence name.
    /// </summary>
    /// <param name="name">The sequence name.</param>
    /// <returns>A stable 64 bit key.</returns>
    /// <remarks>
    /// FNV-1a over the UTF-16 code units, computed here rather than with PostgreSQL's
    /// <c>hashtext</c>: that function is an undocumented internal whose result is not promised to be
    /// stable across major versions, and a lock key that changes during an upgrade would let two
    /// processes claim the same block during exactly the window when nobody is watching.
    /// </remarks>
    private static long AdvisoryLockKey(string name)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        ulong hash = OffsetBasis;
        foreach (char c in name)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= Prime;
            hash ^= (byte)(c >> 8);
            hash *= Prime;
        }

        return unchecked((long)hash);
    }

    /// <summary>Claims the next block of counter values.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inclusive bounds of the claimed block.</returns>
    /// <exception cref="InvalidOperationException">The counter space is exhausted.</exception>
    private async Task<(long Start, long End)> ClaimBlockAsync(CancellationToken cancellationToken)
    {
        DlePersistenceOptions options = _options.Value;
        string name = options.SlugSequenceName;
        int size = options.SlugBlockSize;

        using IServiceScope scope = _scopeFactory.CreateScope();
        DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();

        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            async token =>
            {
                await using IDbContextTransaction transaction =
                    await db.Database.BeginTransactionAsync(token);

                // Serialises claimants across every process. The lock is transactional, so it is
                // released by the commit or the rollback and cannot be leaked by a crash.
                long lockKey = AdvisoryLockKey(name);
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock({lockKey})",
                    token);

                long? highest = await db.SlugSequences
                    .Where(s => s.Name == name)
                    .MaxAsync(s => (long?)s.BlockEnd, token);

                long start = (highest ?? -1L) + 1L;
                long end = start + size - 1L;

                if (end > MaxSequenceValue)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The slug counter space '{name}' is exhausted at " +
                        $"{MaxSequenceValue} values. Start a new named space, or widen the slug " +
                        $"beyond eight characters."));
                }

                db.SlugSequences.Add(new SlugSequence
                {
                    Name = name,
                    BlockStart = start,
                    BlockEnd = end,
                    ClaimedBy = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Environment.MachineName}/{Environment.ProcessId}"),
                    ClaimedAt = _timeProvider.GetUtcNow(),
                });

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return (start, end);
            },
            cancellationToken);
    }
}
