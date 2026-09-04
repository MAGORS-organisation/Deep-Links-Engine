namespace Dle.Control.Infrastructure;

/// <summary>
/// Mints the 64-bit, time-ordered identifiers of the <c>links</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The identifier has to be known before the insert, so that the response, the revision row and the
/// audit entry can all be written without reading anything back (see
/// <c>LinkConfiguration</c>: the column is <c>ValueGeneratedNever</c>). A database sequence cannot
/// supply that, and a random 64-bit value would scatter inserts across the primary key index.
/// </para>
/// <para>
/// Layout, most significant bit first: one unused sign bit, 41 bits of milliseconds since the epoch
/// below, 10 bits of node identifier, 12 bits of sequence. That gives 4096 identifiers per
/// millisecond per node and lasts until 2095. The sign bit stays zero so the value is positive in
/// every language a client might read it from, including the ones with no unsigned 64-bit type.
/// </para>
/// <para>
/// Time comes from <see cref="TimeProvider"/>, never from <c>DateTime.UtcNow</c>
/// (SHARED-KERNEL §17.2). A clock that steps backwards does not produce duplicates: the generator
/// keeps issuing from the last millisecond it saw rather than rewinding, which costs ordering
/// accuracy during the step and never costs uniqueness.
/// </para>
/// </remarks>
public sealed class SnowflakeIdGenerator
{
    /// <summary>Epoch the timestamp component counts from: 2026-01-01T00:00:00Z.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Bits reserved for the node identifier.</summary>
    public const int NodeBits = 10;

    /// <summary>Bits reserved for the per-millisecond sequence.</summary>
    public const int SequenceBits = 12;

    private const long NodeMask = (1L << NodeBits) - 1;
    private const long SequenceMask = (1L << SequenceBits) - 1;

    private readonly TimeProvider _timeProvider;
    private readonly long _node;
    private readonly Lock _gate = new();

    private long _lastMilliseconds = -1;
    private long _sequence;

    /// <summary>Creates the generator.</summary>
    /// <param name="timeProvider">Clock supplying the timestamp component.</param>
    /// <param name="nodeId">
    /// Identifier of this process within the deployment, 0 to 1023. Two processes sharing one node
    /// identifier can mint the same value in the same millisecond, so a multi-replica deployment
    /// gives each replica its own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> is out of range.</exception>
    public SnowflakeIdGenerator(TimeProvider timeProvider, int nodeId)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(nodeId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeId, (int)NodeMask);

        _timeProvider = timeProvider;
        _node = nodeId;
    }

    /// <summary>Mints the next identifier.</summary>
    /// <returns>A positive, time-ordered identifier.</returns>
    /// <exception cref="InvalidOperationException">The clock is before <see cref="Epoch"/>.</exception>
    public long Next()
    {
        long milliseconds = (long)(_timeProvider.GetUtcNow() - Epoch).TotalMilliseconds;

        if (milliseconds < 0)
        {
            throw new InvalidOperationException(
                "The clock is before the identifier epoch, so no identifier can be minted. " +
                "Check the host clock before creating links.");
        }

        lock (_gate)
        {
            if (milliseconds > _lastMilliseconds)
            {
                _lastMilliseconds = milliseconds;
                _sequence = 0;
            }
            else
            {
                // Either the same millisecond or a clock that went backwards. Both are handled by
                // advancing the sequence and, when it wraps, moving to the next millisecond.
                _sequence = (_sequence + 1) & SequenceMask;

                if (_sequence == 0)
                {
                    _lastMilliseconds++;
                }
            }

            return (_lastMilliseconds << (NodeBits + SequenceBits))
                | (_node << SequenceBits)
                | _sequence;
        }
    }
}
