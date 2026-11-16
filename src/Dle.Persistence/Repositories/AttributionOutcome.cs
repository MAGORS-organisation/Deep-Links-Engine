namespace Dle.Persistence.Repositories;

/// <summary>
/// What happened when an attribution was offered to the store.
/// </summary>
public enum AttributionOutcome
{
    /// <summary>The attribution was written.</summary>
    Created = 0,

    /// <summary>
    /// The installation already had an attribution, which is returned unchanged. A repeated resolve
    /// is idempotent rather than a second credit.
    /// </summary>
    AlreadyAttributed = 1,

    /// <summary>
    /// The click had already been credited to a different installation, so nothing was written. One
    /// click is worth at most one install, and a double claim is the cheapest attribution fraud
    /// there is.
    /// </summary>
    ClickAlreadyClaimed = 2,
}
