namespace Dle.Domain.Analytics;

/// <summary>
/// Bucket size of a time series report (FR-202). Buckets are always aligned in UTC.
/// </summary>
public enum TimeGrain
{
    /// <summary>One bucket per hour.</summary>
    Hour = 0,

    /// <summary>One bucket per calendar day.</summary>
    Day = 1,

    /// <summary>One bucket per ISO week.</summary>
    Week = 2,

    /// <summary>One bucket per calendar month.</summary>
    Month = 3,
}
