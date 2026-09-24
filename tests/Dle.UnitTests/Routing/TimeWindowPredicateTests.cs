using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-126. The window is half open: inclusive at From, exclusive at To, so two adjacent campaign
/// windows never both match one instant. Hours and days are UTC sets, not ranges.
/// </summary>
public sealed class TimeWindowPredicateTests
{
    /// <summary>Saturday 2026-03-14, 12:30 UTC. Hour 12, DayOfWeek 6.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Matches_EmptyPredicate_MatchesAnyInstant()
    {
        Assert.True(new TimeWindowPredicate().Matches(Now));
        Assert.True(new TimeWindowPredicate().Matches(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Matches_AtTheStartOfTheWindow_IsInside()
    {
        Assert.True(new TimeWindowPredicate { From = Now }.Matches(Now));
    }

    [Fact]
    public void Matches_OneTickBeforeTheStart_IsOutside()
    {
        Assert.False(new TimeWindowPredicate { From = Now }.Matches(Now.AddTicks(-1)));
    }

    [Fact]
    public void Matches_AtTheEndOfTheWindow_IsAlreadyOutside()
    {
        Assert.False(new TimeWindowPredicate { To = Now }.Matches(Now));
        Assert.True(new TimeWindowPredicate { To = Now }.Matches(Now.AddTicks(-1)));
    }

    [Fact]
    public void Matches_ClosedWindow_IsAnAndOfBothEnds()
    {
        TimeWindowPredicate window = new() { From = Now.AddHours(-1), To = Now.AddHours(1) };

        Assert.True(window.Matches(Now));
        Assert.False(window.Matches(Now.AddHours(-2)));
        Assert.False(window.Matches(Now.AddHours(2)));
    }

    [Fact]
    public void Matches_ComparesInstantsNotLocalWallClock()
    {
        // 13:00+01:00 is 12:00 UTC, half an hour before Now.
        TimeWindowPredicate window = new() { From = new DateTimeOffset(2026, 3, 14, 13, 0, 0, TimeSpan.FromHours(1)) };

        Assert.True(window.Matches(Now));
        Assert.True(window.Matches(new DateTimeOffset(2026, 3, 14, 13, 30, 0, TimeSpan.FromHours(1))));
        Assert.False(window.Matches(new DateTimeOffset(2026, 3, 14, 12, 30, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void Matches_HourSet_IsAMembershipTestNotARange()
    {
        TimeWindowPredicate window = new() { HoursUtc = [9, 12, 20] };

        Assert.True(window.Matches(Now));
        Assert.True(window.Matches(Now.AddHours(-3)));
        Assert.False(window.Matches(Now.AddHours(-2)));
        Assert.False(window.Matches(Now.AddHours(1)));
    }

    [Fact]
    public void Matches_HourSetBoundaries_CoverMidnightAndTheLastHour()
    {
        TimeWindowPredicate window = new() { HoursUtc = [0, 23] };

        Assert.True(window.Matches(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(window.Matches(new DateTimeOffset(2026, 3, 14, 23, 59, 59, TimeSpan.Zero)));
        Assert.False(window.Matches(new DateTimeOffset(2026, 3, 14, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Matches_EmptyHourSet_IsTreatedAsAbsent()
    {
        Assert.True(new TimeWindowPredicate { HoursUtc = [] }.Matches(Now));
    }

    [Fact]
    public void Matches_DayOfWeekSet_UsesZeroForSunday()
    {
        // 2026-03-14 is a Saturday, so DayOfWeek is 6.
        Assert.True(new TimeWindowPredicate { DaysOfWeekUtc = [6] }.Matches(Now));
        Assert.False(new TimeWindowPredicate { DaysOfWeekUtc = [0] }.Matches(Now));
        Assert.True(new TimeWindowPredicate { DaysOfWeekUtc = [0] }.Matches(Now.AddDays(1)));
        Assert.True(new TimeWindowPredicate { DaysOfWeekUtc = [1, 2, 3, 4, 5] }.Matches(Now.AddDays(2)));
    }

    [Fact]
    public void Matches_EmptyDaySet_IsTreatedAsAbsent()
    {
        Assert.True(new TimeWindowPredicate { DaysOfWeekUtc = [] }.Matches(Now));
    }

    [Fact]
    public void Matches_HoursAndDaysAndRange_AreCombinedWithAnd()
    {
        TimeWindowPredicate window = new()
        {
            From = Now.AddDays(-7),
            To = Now.AddDays(7),
            HoursUtc = [12],
            DaysOfWeekUtc = [6],
        };

        Assert.True(window.Matches(Now));
        Assert.False(window.Matches(Now.AddHours(1)));      // right day, wrong hour
        Assert.False(window.Matches(Now.AddDays(1)));       // right hour, wrong day
        Assert.False(window.Matches(Now.AddDays(14)));      // outside the range
    }

    [Fact]
    public void Matches_HourSetSpanningMidnight_IsExpressedAsTwoMembers()
    {
        TimeWindowPredicate window = new() { HoursUtc = [23, 0] };

        Assert.True(window.Matches(new DateTimeOffset(2026, 3, 14, 23, 10, 0, TimeSpan.Zero)));
        Assert.True(window.Matches(new DateTimeOffset(2026, 3, 15, 0, 10, 0, TimeSpan.Zero)));
        Assert.False(window.Matches(new DateTimeOffset(2026, 3, 15, 1, 10, 0, TimeSpan.Zero)));
    }
}
