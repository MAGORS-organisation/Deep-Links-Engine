using Dle.Domain.Clients;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-121 to FR-126. Within one field the values are an OR; across fields they are an AND; an
/// absent field means "do not care". Getting that backwards silently reroutes whole markets, so
/// each axis is pinned individually and then in combination.
/// </summary>
public sealed class RoutingEngineConditionTests
{
    private static readonly RoutingEngine Engine = new(new FrozenClock(TestClients.Now));
    private const string ClickId = "clk_conditions";

    // ---------------------------------------------------------------- platform

    [Theory]
    [InlineData(Platform.Ios, "ios", true)]
    [InlineData(Platform.Android, "android", true)]
    [InlineData(Platform.Desktop, "desktop", true)]
    [InlineData(Platform.Other, "other", true)]
    [InlineData(Platform.Unknown, "unknown", true)]
    [InlineData(Platform.Ios, "android", false)]
    [InlineData(Platform.Desktop, "ios", false)]
    [InlineData(Platform.Unknown, "ios", false)]
    public void Evaluate_PlatformCondition_UsesTheLowercaseEnumName(Platform platform, string ruleValue, bool expectMatch)
    {
        AssertMatch(new RuleCondition { Platform = [ruleValue] }, TestClients.Client(platform), expectMatch);
    }

    [Fact]
    public void Evaluate_PlatformCondition_IsAnOrWithinTheField()
    {
        RuleCondition when = new() { Platform = ["ios", "android"] };

        AssertMatch(when, TestClients.Client(Platform.Ios), expectMatch: true);
        AssertMatch(when, TestClients.Client(Platform.Android), expectMatch: true);
        AssertMatch(when, TestClients.Client(Platform.Desktop), expectMatch: false);
    }

    [Fact]
    public void Evaluate_PlatformCondition_TrimsAndIgnoresCaseInTheAuthoredValue()
    {
        AssertMatch(new RuleCondition { Platform = ["  IOS  "] }, TestClients.Client(Platform.Ios), expectMatch: true);
    }

    // ---------------------------------------------------------------- country and region

    [Fact]
    public void Evaluate_CountryCondition_MatchesTheUppercaseIsoCode()
    {
        RuleCondition when = new() { Country = ["SK", "CZ"] };

        AssertMatch(when, TestClients.Client(country: "SK"), expectMatch: true);
        AssertMatch(when, TestClients.Client(country: "CZ"), expectMatch: true);
        AssertMatch(when, TestClients.Client(country: "AT"), expectMatch: false);
    }

    [Fact]
    public void Evaluate_CountryCondition_ComparesCaseInsensitively()
    {
        AssertMatch(new RuleCondition { Country = ["SK"] }, TestClients.Client(country: "sk"), expectMatch: true);
    }

    [Fact]
    public void Evaluate_CountryCondition_ClientWithoutAGeoLookup_DoesNotMatch()
    {
        // GeoIP missing or the file is corrupt (chaos matrix section D.6): the rule must fall through
        // to the default rather than match on a null country.
        AssertMatch(new RuleCondition { Country = ["SK"] }, TestClients.Client(country: null), expectMatch: false);
    }

    [Fact]
    public void Evaluate_RegionCondition_BehavesLikeCountry()
    {
        RuleCondition when = new() { Region = ["BA", "KE"] };

        AssertMatch(when, TestClients.Client(region: "BA"), expectMatch: true);
        AssertMatch(when, TestClients.Client(region: "ZA"), expectMatch: false);
        AssertMatch(when, TestClients.Client(region: null), expectMatch: false);
    }

    // ---------------------------------------------------------------- language

    [Theory]
    [InlineData("sk", "sk", true)]
    [InlineData("sk", "sk-SK", true)]
    [InlineData("sk-SK", "sk", true)]
    [InlineData("de-AT", "de-DE", true)]
    [InlineData("sk", "cs", false)]
    [InlineData("sk", "sl", false)]
    [InlineData("sk", null, false)]
    [InlineData("sk", "", false)]
    public void Evaluate_LanguageCondition_ComparesOnlyThePrimarySubtag(string ruleValue, string? clientLanguage, bool expectMatch)
    {
        AssertMatch(new RuleCondition { Language = [ruleValue] }, TestClients.Client(language: clientLanguage), expectMatch);
    }

    [Fact]
    public void Evaluate_LanguageCondition_IsAnOrWithinTheField()
    {
        RuleCondition when = new() { Language = ["sk", "cs"] };

        AssertMatch(when, TestClients.Client(language: "cs-CZ"), expectMatch: true);
        AssertMatch(when, TestClients.Client(language: "hu"), expectMatch: false);
    }

    [Fact]
    public void Evaluate_LanguageCondition_AcceptsAnUnderscoreSeparatedTag()
    {
        AssertMatch(new RuleCondition { Language = ["sk"] }, TestClients.Client(language: "sk_SK"), expectMatch: true);
    }

    // ---------------------------------------------------------------- channel

    [Theory]
    [InlineData(ClientChannel.Browser, ChannelNames.Browser, true)]
    [InlineData(ClientChannel.InAppInstagram, ChannelNames.InAppInstagram, true)]
    [InlineData(ClientChannel.InAppFacebook, ChannelNames.InAppFacebook, true)]
    [InlineData(ClientChannel.InAppTwitter, ChannelNames.InAppTwitter, true)]
    [InlineData(ClientChannel.NativeApp, ChannelNames.NativeApp, true)]
    [InlineData(ClientChannel.Crawler, ChannelNames.Crawler, true)]
    [InlineData(ClientChannel.Browser, ChannelNames.InAppFacebook, false)]
    [InlineData(ClientChannel.InAppInstagram, ChannelNames.InAppFacebook, false)]
    public void Evaluate_ChannelCondition_UsesTheCanonicalWireName(ClientChannel channel, string ruleValue, bool expectMatch)
    {
        AssertMatch(new RuleCondition { Channel = [ruleValue] }, TestClients.Client(channel: channel), expectMatch);
    }

    // ---------------------------------------------------------------- versions

    [Fact]
    public void Evaluate_OsVersionCondition_UsesTheVersionComparer()
    {
        RuleCondition when = new() { OsVersion = new VersionPredicate { Gte = "18" } };

        AssertMatch(when, TestClients.Client(osVersion: "18"), expectMatch: true);
        AssertMatch(when, TestClients.Client(osVersion: "18.1"), expectMatch: true);
        AssertMatch(when, TestClients.Client(osVersion: "26.0"), expectMatch: true);
        AssertMatch(when, TestClients.Client(osVersion: "17.6.1"), expectMatch: false);
        AssertMatch(when, TestClients.Client(osVersion: null), expectMatch: false);
    }

    [Fact]
    public void Evaluate_AppVersionCondition_IsIndependentOfTheOsVersion()
    {
        RuleCondition when = new() { AppVersion = new VersionPredicate { Lt = "3.0" } };

        AssertMatch(when, TestClients.Client(appVersion: "2.9.9", osVersion: "26"), expectMatch: true);
        AssertMatch(when, TestClients.Client(appVersion: "3.0", osVersion: "1"), expectMatch: false);
    }

    // ---------------------------------------------------------------- absent field means do not care

    [Fact]
    public void Evaluate_AbsentField_DoesNotConstrainTheMatch()
    {
        RuleCondition when = new() { Platform = ["ios"] };

        // Country, language, channel and both version fields are unset in the rule; the client sets
        // values for all of them and still matches.
        AssertMatch(
            when,
            TestClients.Client(Platform.Ios, ClientChannel.InAppTikTok, "JP", "13", "ja-JP", "26.1", "9.9.9"),
            expectMatch: true);
    }

    [Fact]
    public void Evaluate_EmptyArrayField_IsTreatedAsAbsent()
    {
        AssertMatch(new RuleCondition { Country = [] }, TestClients.Client(country: null), expectMatch: true);
        AssertMatch(new RuleCondition { Platform = [] }, TestClients.Client(Platform.Desktop), expectMatch: true);
    }

    // ---------------------------------------------------------------- AND across fields

    [Fact]
    public void Evaluate_MultipleFields_MustAllMatch()
    {
        RuleCondition when = new()
        {
            Platform = ["ios"],
            Country = ["SK"],
            Language = ["sk"],
            Channel = [ChannelNames.Browser],
            OsVersion = new VersionPredicate { Gte = "18" },
        };

        AssertMatch(when, Full(), expectMatch: true);
        AssertMatch(when, Full() with { Platform = Platform.Android }, expectMatch: false);
        AssertMatch(when, Full() with { Country = "CZ" }, expectMatch: false);
        AssertMatch(when, Full() with { Language = "hu" }, expectMatch: false);
        AssertMatch(when, Full() with { Channel = ClientChannel.InAppFacebook }, expectMatch: false);
        AssertMatch(when, Full() with { OsVersion = "17.0" }, expectMatch: false);
    }

    [Fact]
    public void Evaluate_MultipleFieldsWithOrsInside_CombinesBothLaws()
    {
        RuleCondition when = new() { Platform = ["ios", "android"], Country = ["SK", "CZ"] };

        AssertMatch(when, TestClients.Client(Platform.Android, country: "CZ"), expectMatch: true);
        AssertMatch(when, TestClients.Client(Platform.Ios, country: "SK"), expectMatch: true);
        AssertMatch(when, TestClients.Client(Platform.Desktop, country: "SK"), expectMatch: false);
        AssertMatch(when, TestClients.Client(Platform.Ios, country: "AT"), expectMatch: false);
    }

    private static ClientContext Full() =>
        TestClients.Client(Platform.Ios, ClientChannel.Browser, "SK", "BA", "sk-SK", "18.2");

    private static void AssertMatch(RuleCondition when, ClientContext client, bool expectMatch)
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule("candidate", when, TestClients.Web("https://candidate.example.com/")),
            TestClients.DefaultRule(),
        ];

        RoutingDecision decision = Engine.Evaluate(rules, client, TestClients.FullConsent, ClickId);

        Assert.Equal(expectMatch ? "candidate" : "default", decision.MatchedRuleId);
    }
}
