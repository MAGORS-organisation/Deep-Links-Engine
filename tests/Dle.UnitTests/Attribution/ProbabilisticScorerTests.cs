using Dle.Domain.Attribution;
using Xunit;

namespace Dle.UnitTests.Attribution;

/// <summary>
/// TC-147 and FR-186. Probabilistic attribution is the part of the product that is allowed to be
/// wrong, which is exactly why it must never quietly widen. Outside the window the score is zero,
/// not a "weak match"; a signal that is missing on either side contributes nothing and its weight is
/// not redistributed onto the signals that did match.
/// </summary>
public sealed class ProbabilisticScorerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    private static readonly DeviceSignals Reference = new()
    {
        IpPrefix = "192.0.2.0/24",
        OsVersion = "18.1",
        Language = "sk-SK",
        TimezoneOffsetMinutes = 60,
        Screen = "1080x2400",
    };

    // ================================================================ TC-147: the window is a wall

    [Theory]
    [Trait("TestCase", "TC-147")]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(90)]
    [InlineData(1440)]
    public void Score_AtOrBeyondTheWindow_IsZeroAndNeverAWeakMatch(int elapsedMinutes)
    {
        decimal score = ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(elapsedMinutes), Window);

        Assert.Equal(0m, score);
    }

    [Fact]
    [Trait("TestCase", "TC-147")]
    public void Score_NinetyMinutesInASixtyMinuteWindow_IsZeroForAPerfectSignalMatch()
    {
        // The signals agree on every axis. The only reason to refuse is the window, and it must win.
        Assert.Equal(0m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(90), Window));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Score_NonPositiveWindow_IsZero(int windowMinutes)
    {
        Assert.Equal(0m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.Zero, TimeSpan.FromMinutes(windowMinutes)));
    }

    [Fact]
    public void Score_DecaysLinearlyAcrossTheWindow()
    {
        Assert.Equal(1.00m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.Zero, Window));
        Assert.Equal(0.75m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(15), Window));
        Assert.Equal(0.50m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(30), Window));
        Assert.Equal(0.25m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(45), Window));
        Assert.Equal(0.02m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(59), Window));
    }

    [Fact]
    public void Score_IsMonotonicallyNonIncreasingInElapsedTime()
    {
        decimal previous = decimal.MaxValue;
        for (int minutes = 0; minutes <= 60; minutes++)
        {
            decimal score = ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(minutes), Window);
            Assert.True(score <= previous, $"The score rose at minute {minutes}.");
            previous = score;
        }
    }

    [Fact]
    public void Score_NegativeElapsedTime_IsTreatedAsZeroRatherThanBoostingTheScore()
    {
        Assert.Equal(1.00m, ProbabilisticScorer.Score(Reference, Reference, TimeSpan.FromMinutes(-5), Window));
    }

    // ================================================================ weights

    [Fact]
    public void Weights_AreTheDocumentedDefaultsAndSumToOne()
    {
        Assert.Equal(0.45m, ProbabilisticScorer.IpPrefixWeight);
        Assert.Equal(0.20m, ProbabilisticScorer.OsVersionWeight);
        Assert.Equal(0.15m, ProbabilisticScorer.LanguageWeight);
        Assert.Equal(0.10m, ProbabilisticScorer.TimezoneWeight);
        Assert.Equal(0.10m, ProbabilisticScorer.ScreenWeight);

        Assert.Equal(
            1.00m,
            ProbabilisticScorer.IpPrefixWeight
            + ProbabilisticScorer.OsVersionWeight
            + ProbabilisticScorer.LanguageWeight
            + ProbabilisticScorer.TimezoneWeight
            + ProbabilisticScorer.ScreenWeight);
    }

    [Fact]
    public void Score_PerfectMatchAtZeroElapsed_ReachesButNeverExceedsTheWeightSum()
    {
        decimal score = ProbabilisticScorer.Score(Reference, Reference, TimeSpan.Zero, Window);

        Assert.Equal(1.00m, score);
        Assert.InRange(score, 0m, 1m);
    }

    [Theory]
    [InlineData("IpPrefix", 0.45)]
    [InlineData("OsVersion", 0.20)]
    [InlineData("Language", 0.15)]
    [InlineData("Timezone", 0.10)]
    [InlineData("Screen", 0.10)]
    public void Score_SingleMatchingSignal_ContributesExactlyItsWeight(string signal, double expected)
    {
        DeviceSignals candidate = OnlyOne(signal);
        DeviceSignals click = OnlyOne(signal);

        Assert.Equal((decimal)expected, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_NoSignalMatches_IsZero()
    {
        DeviceSignals other = new()
        {
            IpPrefix = "198.51.100.0/24",
            OsVersion = "17.0",
            Language = "hu-HU",
            TimezoneOffsetMinutes = -300,
            Screen = "750x1334",
        };

        Assert.Equal(0m, ProbabilisticScorer.Score(Reference, other, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_EmptySignalsOnBothSides_IsZero()
    {
        Assert.Equal(0m, ProbabilisticScorer.Score(new DeviceSignals(), new DeviceSignals(), TimeSpan.Zero, Window));
    }

    // ================================================================ missing signals

    [Fact]
    public void Score_SignalMissingOnTheCandidateSide_ContributesZeroWithoutRedistribution()
    {
        DeviceSignals candidate = Reference with { Screen = null };

        // Only the screen weight is lost: 1.00 - 0.10.
        Assert.Equal(0.90m, ProbabilisticScorer.Score(candidate, Reference, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_SignalMissingOnTheClickSide_ContributesZeroWithoutRedistribution()
    {
        DeviceSignals click = Reference with { IpPrefix = null };

        Assert.Equal(0.55m, ProbabilisticScorer.Score(Reference, click, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_SignalMissingOnBothSides_StillContributesZero()
    {
        DeviceSignals both = Reference with { TimezoneOffsetMinutes = null };

        Assert.Equal(0.90m, ProbabilisticScorer.Score(both, both, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_BlankSignalValue_CountsAsMissing()
    {
        DeviceSignals blank = Reference with { IpPrefix = "   ", Screen = string.Empty };

        Assert.Equal(0.45m, ProbabilisticScorer.Score(blank, blank, TimeSpan.Zero, Window));
    }

    [Fact]
    public void Score_DeviceModelIsNotWeighted()
    {
        DeviceSignals candidate = Reference with { DeviceModel = "iPhone17,1" };
        DeviceSignals click = Reference with { DeviceModel = "SM-S928B" };

        Assert.Equal(1.00m, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    // ================================================================ per-signal comparison rules

    [Theory]
    [InlineData("18.1", "18.1", true)]
    [InlineData("18.1", "18.1.0", true)]
    [InlineData("18.1", "18.01", true)]
    [InlineData("18.1-beta", "18.1", true)]
    [InlineData("18.1", "18.2", false)]
    public void Score_OsVersion_UsesTheVersionComparerNotStringEquality(string left, string right, bool matches)
    {
        DeviceSignals candidate = new() { OsVersion = left };
        DeviceSignals click = new() { OsVersion = right };

        Assert.Equal(matches ? 0.20m : 0m, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    [Theory]
    [InlineData("sk-SK", "sk", true)]
    [InlineData("sk", "sk-SK", true)]
    [InlineData("SK", "sk", true)]
    [InlineData("de-AT", "de-DE", true)]
    [InlineData("sk_SK", "sk", true)]
    [InlineData("sk", "cs", false)]
    public void Score_Language_ComparesOnlyThePrimarySubtag(string left, string right, bool matches)
    {
        DeviceSignals candidate = new() { Language = left };
        DeviceSignals click = new() { Language = right };

        Assert.Equal(matches ? 0.15m : 0m, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    [Theory]
    [InlineData("1080x2400", "1080x2400", true)]
    [InlineData("1080X2400", "1080x2400", true)]
    [InlineData(" 1080x2400 ", "1080x2400", true)]
    [InlineData("1080x2400", "2400x1080", false)]
    public void Score_Screen_IsAnOrdinalCaseInsensitiveComparison(string left, string right, bool matches)
    {
        DeviceSignals candidate = new() { Screen = left };
        DeviceSignals click = new() { Screen = right };

        Assert.Equal(matches ? 0.10m : 0m, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    [Theory]
    [InlineData(60, 60, true)]
    [InlineData(0, 0, true)]
    [InlineData(-300, -300, true)]
    [InlineData(60, 120, false)]
    public void Score_TimezoneOffset_MustBeExactlyEqual(int left, int right, bool matches)
    {
        DeviceSignals candidate = new() { TimezoneOffsetMinutes = left };
        DeviceSignals click = new() { TimezoneOffsetMinutes = right };

        Assert.Equal(matches ? 0.10m : 0m, ProbabilisticScorer.Score(candidate, click, TimeSpan.Zero, Window));
    }

    // ================================================================ shape of the result

    [Fact]
    public void Score_IsAlwaysInsideZeroToOneAndRoundedToTwoDecimals()
    {
        for (int elapsed = 0; elapsed <= 120; elapsed += 3)
        {
            foreach (DeviceSignals candidate in Variants())
            {
                decimal score = ProbabilisticScorer.Score(candidate, Reference, TimeSpan.FromMinutes(elapsed), Window);

                Assert.InRange(score, 0m, 1m);
                Assert.Equal(score, Math.Round(score, 2));
            }
        }
    }

    [Fact]
    public void Score_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProbabilisticScorer.Score(null!, Reference, TimeSpan.Zero, Window));
        Assert.Throws<ArgumentNullException>(() => ProbabilisticScorer.Score(Reference, null!, TimeSpan.Zero, Window));
    }

    private static DeviceSignals OnlyOne(string signal) => signal switch
    {
        "IpPrefix" => new DeviceSignals { IpPrefix = Reference.IpPrefix },
        "OsVersion" => new DeviceSignals { OsVersion = Reference.OsVersion },
        "Language" => new DeviceSignals { Language = Reference.Language },
        "Timezone" => new DeviceSignals { TimezoneOffsetMinutes = Reference.TimezoneOffsetMinutes },
        "Screen" => new DeviceSignals { Screen = Reference.Screen },
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };

    private static DeviceSignals[] Variants() =>
    [
        Reference,
        Reference with { Screen = null },
        Reference with { IpPrefix = null, Screen = null },
        Reference with { OsVersion = "17.0" },
        new DeviceSignals(),
    ];
}
