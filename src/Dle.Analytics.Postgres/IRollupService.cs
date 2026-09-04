namespace Dle.Analytics.Postgres;

/// <summary>
/// Maintains the analytics rollup tables. Called by the rollup worker (component C-09); this
/// module registers no background service of its own, so the schedule stays where the rest of the
/// engine's schedules are.
/// </summary>
/// <remarks>
/// A pass is idempotent: whole buckets are recomputed and overwritten rather than incremented, and
/// each pass deliberately re-scans a few hours before the previous watermark so a late-arriving
/// event is still counted. Running the job twice, or running it after a crash, therefore cannot
/// double count. The watermark it publishes is what lets the reporting store decide whether a
/// rollup may answer a query at all.
/// </remarks>
public interface IRollupService
{
    /// <summary>Runs one rollup pass.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the pass aggregated and how far the watermarks advanced.</returns>
    Task<RollupRunResult> RunAsync(CancellationToken ct);
}
