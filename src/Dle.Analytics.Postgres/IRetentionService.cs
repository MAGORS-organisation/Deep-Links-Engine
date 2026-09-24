namespace Dle.Analytics.Postgres;

/// <summary>
/// Enforces the analytics retention policy (FR-247, §E.6.3). Called by the retention worker
/// (component C-09); this module registers no background service of its own.
/// </summary>
/// <remarks>
/// <para>
/// Raw events leave by partition, not by <c>DELETE</c>. Deleting a hundred million rows rewrites
/// indexes, bloats the heap and leaves the data readable until autovacuum catches up, whereas
/// detaching a partition and dropping it is a catalogue operation that actually removes the files.
/// That difference matters more for a privacy control than it does for performance.
/// </para>
/// <para>
/// Aggregates outlive raw events, which is the whole point of keeping them: thirty days of raw
/// click stream and two years of rollups by default. The pass is safe to run repeatedly and safe
/// to interrupt — each partition is removed in its own statement, and the run is recorded whether
/// it succeeded, did nothing, or failed.
/// </para>
/// </remarks>
public interface IRetentionService
{
    /// <summary>Runs one retention pass.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was removed, and the identifier of the audit row that records it.</returns>
    Task<RetentionRunResult> RunAsync(CancellationToken ct);
}
