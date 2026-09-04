namespace Dle.Persistence.Repositories;

/// <summary>
/// What happened when an attribution was offered to the store (TC-143, TC-144).
/// </summary>
public enum AttributionOutcome
{
    /// <summary>The attribution was written.</summary>
    Created = 0,

    /// <summary>
    /// The installation already had an attribution, which is returned unchanged. A repeated resolve
    /// is idempotent rather than a second credit (TC-143, FR-188).
    /// </summary>
    AlreadyAttributed = 1,

    /// <summary>
    /// The click had already been credited to a different installation, so nothing was written. One
    /// click is worth at most one install, and a double claim is the cheapest attribution fraud
    /// there is (TC-144).
    /// </summary>
    ClickAlreadyClaimed = 2,
}
