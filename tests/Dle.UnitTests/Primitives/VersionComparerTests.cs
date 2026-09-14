using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// FR-124 routes on OS and application version. A comparer that is not a total order turns a
/// "min_app_version" rule into a coin toss, so the ordering laws are asserted, not only the
/// individual comparisons. SHARED-KERNEL section 1: missing components are zero, the prerelease
/// suffix is ignored.
/// </summary>
public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("18", "18", 0)]
    [InlineData("18", "18.0", 0)]
    [InlineData("18", "18.0.0", 0)]
    [InlineData("18.0.0.0", "18", 0)]
    [InlineData("18.1", "18", 1)]
    [InlineData("18", "18.1", -1)]
    [InlineData("18.1.2", "18.1.10", -1)]
    [InlineData("2", "10", -1)]
    [InlineData("10", "9", 1)]
    [InlineData("18.1.2", "18.1.2", 0)]
    public void Compare_MissingComponentsCountAsZero(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Compare(left, right)));
    }

    [Theory]
    [InlineData("3.4.1-beta", "3.4.1", 0)]
    [InlineData("3.4.1-beta.2", "3.4.1-rc.1", 0)]
    [InlineData("3.4.1+build.99", "3.4.1", 0)]
    [InlineData("3.4.2-beta", "3.4.1", 1)]
    [InlineData("3.4.0-beta", "3.4.1", -1)]
    public void Compare_PrereleaseAndBuildSuffixes_AreIgnored(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Compare(left, right)));
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("", "", 0)]
    [InlineData(null, "", 0)]
    [InlineData("   ", "1.0", -1)]
    [InlineData(null, "1.0", -1)]
    [InlineData("1.0", null, 1)]
    public void Compare_MissingVersion_SortsBeforeAnyVersion(string? left, string? right, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Compare(left, right)));
    }

    [Fact]
    public void Compare_NonNumericComponent_IsTreatedAsZero()
    {
        Assert.Equal(0, VersionComparer.Compare("18.x", "18.0"));
        Assert.Equal(0, VersionComparer.Compare("18.x.1", "18.0.1"));
    }

    [Fact]
    public void Compare_ComponentBeyondUInt64_SaturatesInsteadOfOverflowing()
    {
        Assert.Equal(0, Math.Sign(VersionComparer.Compare("99999999999999999999999", "99999999999999999999998")));
        Assert.Equal(1, Math.Sign(VersionComparer.Compare("99999999999999999999999", "18")));
    }

    [Fact]
    public void Compare_IsAntisymmetric()
    {
        string[] versions = Ladder();
        foreach (string left in versions)
        {
            foreach (string right in versions)
            {
                Assert.Equal(-Math.Sign(VersionComparer.Compare(right, left)), Math.Sign(VersionComparer.Compare(left, right)));
            }
        }
    }

    [Fact]
    public void Compare_IsTransitive()
    {
        string[] versions = Ladder();
        foreach (string a in versions)
        {
            foreach (string b in versions)
            {
                foreach (string c in versions)
                {
                    int ab = Math.Sign(VersionComparer.Compare(a, b));
                    int bc = Math.Sign(VersionComparer.Compare(b, c));
                    if (ab <= 0 && bc <= 0)
                    {
                        Assert.True(
                            Math.Sign(VersionComparer.Compare(a, c)) <= 0,
                            $"{a} <= {b} <= {c} but the comparer put {a} after {c}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Compare_SortsAKnownLadderIntoTheExpectedOrder()
    {
        // Every entry compares distinctly, so the expected order does not depend on sort stability.
        string[] versions = ["18.1.2", "18", "3.4.1-beta", "18.1", "9.0"];
        string[] expected = ["3.4.1-beta", "9.0", "18", "18.1", "18.1.2"];

        Array.Sort(versions, VersionComparer.Compare);

        Assert.Equal(expected, versions);
    }

    [Theory]
    [InlineData("18", "18")]
    [InlineData("18.1", "18.1")]
    [InlineData("18.01", "18.1")]
    [InlineData("3.4.1-beta", "3.4.1")]
    [InlineData("  3.4.1  ", "3.4.1")]
    [InlineData("18.x", "18.0")]
    public void TryNormalize_ProducesACanonicalDottedForm(string raw, string expected)
    {
        Assert.True(VersionComparer.TryNormalize(raw, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("beta")]
    [InlineData("v18")]
    [InlineData("-beta")]
    public void TryNormalize_InputWithoutANumericComponent_IsRejected(string? raw)
    {
        Assert.False(VersionComparer.TryNormalize(raw, out string normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void TryNormalize_ExcessivelyLongInput_IsRejected()
    {
        Assert.False(VersionComparer.TryNormalize(new string('1', 129), out _));
    }

    [Fact]
    public void TryNormalize_IsIdempotent()
    {
        Assert.True(VersionComparer.TryNormalize("18.01.0-rc.1", out string once));
        Assert.True(VersionComparer.TryNormalize(once, out string twice));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void TryNormalize_PreservesTheComparisonResult()
    {
        Assert.True(VersionComparer.TryNormalize("18.1-beta", out string left));
        Assert.True(VersionComparer.TryNormalize("18.01.0", out string right));

        Assert.Equal(0, VersionComparer.Compare(left, right));
    }

    private static string[] Ladder() =>
        ["", "0", "1", "1.0", "1.0.1", "3.4.1-beta", "3.4.1", "9", "10", "18", "18.1", "18.1.2", "18.x"];
}
