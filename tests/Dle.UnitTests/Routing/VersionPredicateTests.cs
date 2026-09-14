using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-124. A version predicate with no bound at all is vacuously true; a predicate with a bound
/// never matches an unknown version, because "we could not read the version" is not evidence that
/// the version is high enough.
/// </summary>
public sealed class VersionPredicateTests
{
    [Fact]
    public void Matches_PredicateWithoutAnyBound_MatchesEverythingIncludingNull()
    {
        VersionPredicate predicate = new();

        Assert.True(predicate.Matches("18.1"));
        Assert.True(predicate.Matches(null));
        Assert.True(predicate.Matches(string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_UnknownVersionAgainstABoundedPredicate_DoesNotMatch(string? actual)
    {
        Assert.False(new VersionPredicate { Gte = "18" }.Matches(actual));
        Assert.False(new VersionPredicate { Lt = "18" }.Matches(actual));
        Assert.False(new VersionPredicate { Eq = "18" }.Matches(actual));
    }

    [Theory]
    [InlineData("18", true)]
    [InlineData("18.0", true)]
    [InlineData("18.0.0", true)]
    [InlineData("18.1", false)]
    [InlineData("17.9", false)]
    public void Matches_Eq_TreatsMissingComponentsAsZero(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Eq = "18" }.Matches(actual));
    }

    [Theory]
    [InlineData("18.1", true)]
    [InlineData("18.0.1", true)]
    [InlineData("18", false)]
    [InlineData("17.9", false)]
    public void Matches_Gt_IsStrict(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Gt = "18" }.Matches(actual));
    }

    [Theory]
    [InlineData("18", true)]
    [InlineData("18.0", true)]
    [InlineData("26", true)]
    [InlineData("17.9.9", false)]
    public void Matches_Gte_IsInclusive(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Gte = "18" }.Matches(actual));
    }

    [Theory]
    [InlineData("17.9", true)]
    [InlineData("18", false)]
    [InlineData("18.0.1", false)]
    public void Matches_Lt_IsStrict(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Lt = "18" }.Matches(actual));
    }

    [Theory]
    [InlineData("18", true)]
    [InlineData("18.0.0", true)]
    [InlineData("18.0.1", false)]
    public void Matches_Lte_IsInclusive(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Lte = "18" }.Matches(actual));
    }

    [Fact]
    public void Matches_RangeWithBothEnds_IsAnAnd()
    {
        VersionPredicate predicate = new() { Gte = "18", Lt = "26" };

        Assert.True(predicate.Matches("18"));
        Assert.True(predicate.Matches("25.9"));
        Assert.False(predicate.Matches("26"));
        Assert.False(predicate.Matches("17.9"));
    }

    [Fact]
    public void Matches_ContradictoryRange_NeverMatches()
    {
        VersionPredicate predicate = new() { Gt = "26", Lt = "18" };

        Assert.False(predicate.Matches("18"));
        Assert.False(predicate.Matches("26"));
        Assert.False(predicate.Matches("30"));
    }

    [Theory]
    [InlineData("3.4.1-beta", true)]
    [InlineData("3.4.1+build.7", true)]
    [InlineData("3.4.0-rc.1", false)]
    public void Matches_PrereleaseSuffix_IsIgnored(string actual, bool expected)
    {
        Assert.Equal(expected, new VersionPredicate { Gte = "3.4.1" }.Matches(actual));
    }

    [Fact]
    public void Matches_BoundWithAPrereleaseSuffix_IsAlsoTruncated()
    {
        Assert.True(new VersionPredicate { Eq = "3.4.1-beta" }.Matches("3.4.1"));
    }

    [Fact]
    public void Matches_TwoDigitMinorComponent_IsComparedNumericallyNotLexically()
    {
        VersionPredicate predicate = new() { Gte = "18.9" };

        Assert.True(predicate.Matches("18.10"));
        Assert.False(predicate.Matches("18.8"));
    }
}
