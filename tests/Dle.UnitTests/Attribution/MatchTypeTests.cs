using Dle.Domain.Attribution;
using Xunit;
using MatchType = Dle.Domain.Attribution.MatchType;

namespace Dle.UnitTests.Attribution;

/// <summary>
/// FR-186: every attribution carries a match type and a confidence, and the names are a wire
/// contract shared with the SDK response and the attribution_records table.
/// </summary>
public sealed class MatchTypeTests
{
    [Theory]
    [InlineData(MatchType.None, "none")]
    [InlineData(MatchType.InstallReferrer, "install_referrer")]
    [InlineData(MatchType.Login, "login")]
    [InlineData(MatchType.ClaimCode, "claim_code")]
    [InlineData(MatchType.Probabilistic, "probabilistic")]
    [InlineData(MatchType.DirectOpen, "direct_open")]
    public void From_ProducesTheExactWireName(MatchType type, string expected)
    {
        Assert.Equal(expected, MatchTypeNames.From(type));
    }

    [Fact]
    public void From_UndefinedEnumValue_FallsBackToNone()
    {
        Assert.Equal(MatchTypeNames.None, MatchTypeNames.From((MatchType)99));
    }

    [Fact]
    public void FromThenParse_RoundTripsEveryDeclaredMatchType()
    {
        foreach (MatchType type in Enum.GetValues<MatchType>())
        {
            Assert.Equal(type, MatchTypeNames.Parse(MatchTypeNames.From(type)));
        }
    }

    [Theory]
    [InlineData("INSTALL_REFERRER", MatchType.InstallReferrer)]
    [InlineData("  claim_code  ", MatchType.ClaimCode)]
    [InlineData("Direct_Open", MatchType.DirectOpen)]
    public void Parse_IsTolerantOfCaseAndSurroundingWhitespace(string name, MatchType expected)
    {
        Assert.Equal(expected, MatchTypeNames.Parse(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fingerprint")]
    [InlineData("install-referrer")]
    public void Parse_UnrecognisedName_IsNoneRatherThanAnException(string? name)
    {
        Assert.Equal(MatchType.None, MatchTypeNames.Parse(name));
    }

    [Fact]
    [Trait("TestCase", "TC-142")]
    public void NoMatch_CarriesTheReasonAndNothingElse()
    {
        AttributionResult result = AttributionResult.NoMatch("no_dl_cid_in_referrer");

        Assert.False(result.Matched);
        Assert.Equal(MatchType.None, result.MatchType);
        Assert.Equal(0m, result.Confidence);
        Assert.Null(result.ClickId);
        Assert.Null(result.LinkId);
        Assert.Null(result.DeeplinkPath);
        Assert.Empty(result.Parameters);
        Assert.Equal("no_dl_cid_in_referrer", result.Evidence["reason"]);
    }

    [Fact]
    public void NoMatch_NullReason_StillProducesAnEvidenceEntry()
    {
        Assert.Equal(string.Empty, AttributionResult.NoMatch(null!).Evidence["reason"]);
    }

    [Fact]
    [Trait("TestCase", "TC-141")]
    public void DeterministicMatch_IsExpressedAsConfidenceOne()
    {
        AttributionResult result = new()
        {
            Matched = true,
            MatchType = MatchType.InstallReferrer,
            Confidence = 1.00m,
            ClickId = "01JQ8Z0K7M8T9RV",
            LinkId = 42,
        };

        Assert.True(result.Matched);
        Assert.Equal(1.00m, result.Confidence);
        Assert.Equal("install_referrer", MatchTypeNames.From(result.MatchType));
    }
}
