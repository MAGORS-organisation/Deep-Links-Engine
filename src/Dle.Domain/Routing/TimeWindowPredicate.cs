namespace Dle.Domain.Routing;

/// <summary>
/// Time constraint on a routing rule (FR-126). Every component is evaluated in UTC so that a
/// rule behaves identically on every node, regardless of server locale.
/// </summary>
public sealed record TimeWindowPredicate
{
    /// <summary>Inclusive lower bound of the campaign window.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound of the campaign window.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Hours of the day in UTC during which the rule is live, 0..23. An empty or absent array means "any hour".</summary>
    public int[]? HoursUtc { get; init; }

    /// <summary>Days of the week in UTC during which the rule is live, 0 = Sunday … 6 = Saturday. An empty or absent array means "any day".</summary>
    public int[]? DaysOfWeekUtc { get; init; }

    /// <summary>
    /// Evaluates the window against the current instant.
    /// </summary>
    /// <param name="now">The current instant; it is converted to UTC before every comparison.</param>
    /// <returns><see langword="true"/> when every component that is set holds.</returns>
    public bool Matches(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();

        if (From is { } from && utc < from)
        {
            return false;
        }

        if (To is { } to && utc >= to)
        {
            return false;
        }

        if (HoursUtc is { Length: > 0 } hours && Array.IndexOf(hours, utc.Hour) < 0)
        {
            return false;
        }

        if (DaysOfWeekUtc is { Length: > 0 } days && Array.IndexOf(days, (int)utc.DayOfWeek) < 0)
        {
            return false;
        }

        return true;
    }
}
