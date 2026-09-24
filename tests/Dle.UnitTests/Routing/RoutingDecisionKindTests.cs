using Dle.Domain.Clients;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// ADR-009 and FR-162. The action a customer authored is not the HTTP shape the edge serves: an
/// in-app webview only surfaces a Universal or App Link on a real tap, so app_or_store inside a
/// webview must always become an interstitial with a real anchor, and InterstitialMode.Never must
/// not be able to switch that off.
/// </summary>
public sealed class RoutingDecisionKindTests
{
    private static readonly RoutingEngine Engine = new(new FrozenClock(TestClients.Now));

    // ---------------------------------------------------------------- actions whose kind is fixed

    [Theory]
    [InlineData(RoutingActionKind.Web, InterstitialMode.Auto)]
    [InlineData(RoutingActionKind.Web, InterstitialMode.Always)]
    [InlineData(RoutingActionKind.Web, InterstitialMode.Never)]
    public void Evaluate_WebAction_IsAlwaysAWebDecision(RoutingActionKind action, InterstitialMode mode)
    {
        AssertKindForEveryChannelAndPlatform(action, mode, DecisionKind.Web);
    }

    [Theory]
    [InlineData(InterstitialMode.Auto)]
    [InlineData(InterstitialMode.Always)]
    [InlineData(InterstitialMode.Never)]
    public void Evaluate_StoreOnlyAction_IsAlwaysAStoreDecision(InterstitialMode mode)
    {
        AssertKindForEveryChannelAndPlatform(RoutingActionKind.StoreOnly, mode, DecisionKind.Store);
    }

    [Theory]
    [InlineData(InterstitialMode.Auto)]
    [InlineData(InterstitialMode.Always)]
    [InlineData(InterstitialMode.Never)]
    public void Evaluate_AppOnlyAction_IsAlwaysAnAppDirectDecision(InterstitialMode mode)
    {
        AssertKindForEveryChannelAndPlatform(RoutingActionKind.AppOnly, mode, DecisionKind.AppDirect);
    }

    [Theory]
    [InlineData(InterstitialMode.Auto)]
    [InlineData(InterstitialMode.Always)]
    [InlineData(InterstitialMode.Never)]
    public void Evaluate_BlockAction_IsAlwaysABlockedDecision(InterstitialMode mode)
    {
        AssertKindForEveryChannelAndPlatform(RoutingActionKind.Block, mode, DecisionKind.Blocked);
    }

    // ---------------------------------------------------------------- app_or_store, the interesting one

    [Theory]
    [InlineData(ClientChannel.InAppFacebook)]
    [InlineData(ClientChannel.InAppInstagram)]
    [InlineData(ClientChannel.InAppTikTok)]
    [InlineData(ClientChannel.InAppLinkedIn)]
    [InlineData(ClientChannel.InAppSnapchat)]
    [InlineData(ClientChannel.InAppTwitter)]
    [InlineData(ClientChannel.InAppWhatsApp)]
    [InlineData(ClientChannel.InAppTelegram)]
    [InlineData(ClientChannel.InAppPinterest)]
    [InlineData(ClientChannel.InAppGeneric)]
    [Trait("TestCase", "TC-101")]
    public void Evaluate_AppOrStoreInsideAnInAppWebView_IsAlwaysAnInterstitial(ClientChannel channel)
    {
        foreach (InterstitialMode mode in Enum.GetValues<InterstitialMode>())
        {
            foreach (Platform platform in Enum.GetValues<Platform>())
            {
                Assert.Equal(
                    DecisionKind.Interstitial,
                    Kind(RoutingActionKind.AppOrStore, mode, channel, platform));
            }
        }
    }

    [Theory]
    [InlineData(ClientChannel.Browser)]
    [InlineData(ClientChannel.Crawler)]
    [InlineData(ClientChannel.NativeApp)]
    [InlineData(ClientChannel.Unknown)]
    public void Evaluate_AppOrStoreWithInterstitialAlways_IsAnInterstitialOutsideWebViewsToo(ClientChannel channel)
    {
        foreach (Platform platform in Enum.GetValues<Platform>())
        {
            Assert.Equal(
                DecisionKind.Interstitial,
                Kind(RoutingActionKind.AppOrStore, InterstitialMode.Always, channel, platform));
        }
    }

    [Theory]
    [InlineData(ClientChannel.Browser)]
    [InlineData(ClientChannel.Crawler)]
    [InlineData(ClientChannel.NativeApp)]
    [InlineData(ClientChannel.Unknown)]
    public void Evaluate_AppOrStoreWithInterstitialNever_GoesStraightToTheStore(ClientChannel channel)
    {
        foreach (Platform platform in Enum.GetValues<Platform>())
        {
            Assert.Equal(
                DecisionKind.Store,
                Kind(RoutingActionKind.AppOrStore, InterstitialMode.Never, channel, platform));
        }
    }

    [Theory]
    [InlineData(Platform.Ios, DecisionKind.Interstitial)]
    [InlineData(Platform.Android, DecisionKind.Interstitial)]
    [InlineData(Platform.Desktop, DecisionKind.Web)]
    [InlineData(Platform.Other, DecisionKind.Web)]
    [InlineData(Platform.Unknown, DecisionKind.Web)]
    public void Evaluate_AppOrStoreOnAuto_SendsMobileToTheInterstitialAndEveryoneElseToTheWeb(
        Platform platform,
        DecisionKind expected)
    {
        foreach (ClientChannel channel in NonWebViewChannels)
        {
            Assert.Equal(expected, Kind(RoutingActionKind.AppOrStore, InterstitialMode.Auto, channel, platform));
        }
    }

    [Fact]
    public void Evaluate_UnknownActionValue_FallsBackToTheWebDecision()
    {
        // Only hand-edited data can produce this. Serving the web fallback never opens an application
        // and never blocks a real user, which is the safe default (SHARED-KERNEL section 17.9).
        Assert.Equal(DecisionKind.Web, Kind((RoutingActionKind)77, InterstitialMode.Auto, ClientChannel.Browser, Platform.Ios));
    }

    [Fact]
    public void Evaluate_UnknownInterstitialModeValue_BehavesLikeAuto()
    {
        Assert.Equal(
            DecisionKind.Interstitial,
            Kind(RoutingActionKind.AppOrStore, (InterstitialMode)77, ClientChannel.Browser, Platform.Ios));
        Assert.Equal(
            DecisionKind.Web,
            Kind(RoutingActionKind.AppOrStore, (InterstitialMode)77, ClientChannel.Browser, Platform.Desktop));
    }

    [Fact]
    public void NotFound_AndGone_AreBlockingSingletonsWithNoRuleId()
    {
        Assert.Equal(DecisionKind.NotFound, RoutingDecision.NotFound.Kind);
        Assert.Equal(DecisionKind.Gone, RoutingDecision.Gone.Kind);
        Assert.Equal(RoutingActionKind.Block, RoutingDecision.NotFound.Action);
        Assert.Equal(RoutingActionKind.Block, RoutingDecision.Gone.Action);
        Assert.Equal(string.Empty, RoutingDecision.NotFound.MatchedRuleId);
        Assert.Equal(string.Empty, RoutingDecision.Gone.MatchedRuleId);
    }

    [Fact]
    public void Blocked_CarriesTheRuleThatBlocked()
    {
        RoutingDecision decision = RoutingDecision.Blocked("geo-block");

        Assert.Equal(DecisionKind.Blocked, decision.Kind);
        Assert.Equal("geo-block", decision.MatchedRuleId);
        Assert.Equal(RoutingActionKind.Block, decision.Action);
    }

    [Fact]
    public void Blocked_NullRuleId_BecomesEmptyRatherThanNull()
    {
        Assert.Equal(string.Empty, RoutingDecision.Blocked(null!).MatchedRuleId);
    }

    private static readonly ClientChannel[] NonWebViewChannels =
    [
        ClientChannel.Browser,
        ClientChannel.Crawler,
        ClientChannel.NativeApp,
        ClientChannel.Unknown,
    ];

    private static void AssertKindForEveryChannelAndPlatform(
        RoutingActionKind action,
        InterstitialMode mode,
        DecisionKind expected)
    {
        foreach (ClientChannel channel in Enum.GetValues<ClientChannel>())
        {
            foreach (Platform platform in Enum.GetValues<Platform>())
            {
                Assert.Equal(expected, Kind(action, mode, channel, platform));
            }
        }
    }

    private static DecisionKind Kind(
        RoutingActionKind action,
        InterstitialMode mode,
        ClientChannel channel,
        Platform platform)
    {
        RuleAction then = new()
        {
            Action = action,
            Url = "https://web.example.com/",
            StoreUrl = "https://play.google.com/store/apps/details?id=sk.example",
            Interstitial = mode,
        };
        List<RoutingRule> rules = [TestClients.Rule("only", null, then)];

        return Engine
            .Evaluate(rules, TestClients.Client(platform, channel), TestClients.FullConsent, "clk_kind")
            .Kind;
    }
}
