namespace Dle.Domain.Ports;

/// <summary>
/// Reads clicks back for attribution.
/// </summary>
public interface IClickLookup
{
    /// <summary>
    /// Finds one click by its public identifier.
    /// </summary>
    /// <param name="clickId">The click identifier taken from the install referrer or a claim.</param>
    /// <param name="hintFrom">Lower bound of the search interval, in UTC.</param>
    /// <param name="hintTo">Upper bound of the search interval, in UTC.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The click, or <see langword="null"/> when it is not in the interval.</returns>
    /// <remarks>
    /// The two hints are not an optimisation, they are the point of the method. The click stream
    /// is partitioned by time, and a lookup without a bound on <c>occurred_at</c> has no pruning
    /// predicate and turns into a scan of every partition. The bounds come from the timestamp
    /// encoded in the click identifier itself (§B.6.3).
    /// </remarks>
    ValueTask<ClickRecord?> FindByClickIdAsync(
        string clickId,
        DateTimeOffset hintFrom,
        DateTimeOffset hintTo,
        CancellationToken ct);

    /// <summary>
    /// Finds candidate clicks for a probabilistic match inside the attribution window.
    /// </summary>
    /// <param name="tenantId">Tenant to search in. Never optional: a candidate from another
    /// tenant must not even be considered.</param>
    /// <param name="from">Start of the window, in UTC.</param>
    /// <param name="to">End of the window, in UTC.</param>
    /// <param name="ipPrefix">Network prefix to pre-filter on, when one is available.</param>
    /// <param name="osFamily">Operating system family to pre-filter on, when one is available.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidates, most recent first. The caller scores them; this method makes no
    /// judgement about which one wins.</returns>
    ValueTask<IReadOnlyList<ClickRecord>> FindCandidatesAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? ipPrefix,
        string? osFamily,
        CancellationToken ct);
}
