using Dle.Domain.Clients;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-127: rule evaluation is deterministic, the first matching rule wins, and a rule without a
/// "when" is the default. Nothing here may depend on the wall clock — the engine is handed a
/// FrozenClock and every client carries an explicit ReceivedAt.
/// </summary>
public sealed class RoutingEngineOrderingTests
{
    private static readonly RoutingEngine Engine = new(new FrozenClock(TestClients.Now));
    private const string ClickId = "clk_ordering";

    [Fact]
    [Trait("TestCase", "TC-105")]
    public void Evaluate_FirstMatchingRuleWins_EvenWhenALaterRuleAlsoMatches()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule("ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://first.example.com/")),
            TestClients.Rule("also-ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://second.example.com/")),
            TestClients.DefaultRule(),
        ];

        RoutingDecision decision = Evaluate(rules, TestClients.Client(Platform.Ios));

        Assert.Equal("ios", decision.MatchedRuleId);
        Assert.Equal("https://first.example.com/", decision.WebUrl);
    }

    [Fact]
    public void Evaluate_ReorderingTheRules_ChangesTheWinner()
    {
        RoutingRule ios = TestClients.Rule(
            "ios",
            new RuleCondition { Platform = ["ios"] },
            TestClients.Web("https://ios.example.com/"));
        RoutingRule sk = TestClients.Rule(
            "sk",
            new RuleCondition { Country = ["SK"] },
            TestClients.Web("https://sk.example.com/"));
        ClientContext client = TestClients.Client(Platform.Ios, country: "SK");

        Assert.Equal("ios", Evaluate([ios, sk, TestClients.DefaultRule()], client).MatchedRuleId);
        Assert.Equal("sk", Evaluate([sk, ios, TestClients.DefaultRule()], client).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_SameInputTwice_ProducesTheSameDecision()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule("ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://ios.example.com/")),
            TestClients.DefaultRule(),
        ];
        ClientContext client = TestClients.Client(Platform.Ios);

        Assert.Equal(Evaluate(rules, client), Evaluate(rules, client));
    }

    [Fact]
    public void Evaluate_NoRuleMatches_FallsThroughToTheDefaultRule()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule("ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://ios.example.com/")),
            TestClients.Rule("android", new RuleCondition { Platform = ["android"] }, TestClients.Web("https://android.example.com/")),
            TestClients.DefaultRule("fallback", "https://web.example.com/"),
        ];

        RoutingDecision decision = Evaluate(rules, TestClients.Client(Platform.Desktop));

        Assert.Equal("fallback", decision.MatchedRuleId);
        Assert.Equal("https://web.example.com/", decision.WebUrl);
        Assert.Equal(DecisionKind.Web, decision.Kind);
    }

    [Fact]
    public void Evaluate_NullWhen_MatchesEveryClient()
    {
        List<RoutingRule> rules = [TestClients.DefaultRule("only")];

        foreach (Platform platform in Enum.GetValues<Platform>())
        {
            Assert.Equal("only", Evaluate(rules, TestClients.Client(platform)).MatchedRuleId);
        }
    }

    [Fact]
    public void Evaluate_ConditionWithNoFieldsSet_AlsoMatchesEveryClient()
    {
        // An empty "when" is not the default rule, but "absent field means do not care" applies to
        // every field at once, so it matches anything.
        List<RoutingRule> rules =
        [
            TestClients.Rule("empty-when", new RuleCondition(), TestClients.Web("https://any.example.com/")),
            TestClients.DefaultRule(),
        ];

        Assert.Equal("empty-when", Evaluate(rules, TestClients.Client(Platform.Desktop, ClientChannel.Crawler)).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_RulesAfterTheDefault_AreUnreachable()
    {
        List<RoutingRule> rules =
        [
            TestClients.DefaultRule("catch-all"),
            TestClients.Rule("ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://ios.example.com/")),
        ];

        Assert.Equal("catch-all", Evaluate(rules, TestClients.Client(Platform.Ios)).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_EmptyRuleList_ReturnsNotFound()
    {
        RoutingDecision decision = Evaluate([], TestClients.Client());

        Assert.Equal(DecisionKind.NotFound, decision.Kind);
        Assert.Equal(string.Empty, decision.MatchedRuleId);
    }

    [Fact]
    public void Evaluate_RuleSetWithoutADefaultAndNoMatch_ReturnsNotFound()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule("ios", new RuleCondition { Platform = ["ios"] }, TestClients.Web("https://ios.example.com/")),
        ];

        Assert.Equal(DecisionKind.NotFound, Evaluate(rules, TestClients.Client(Platform.Android)).Kind);
    }

    [Fact]
    public void Evaluate_MalformedRuleInTheMiddle_IsSkippedRatherThanThrowing()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule { Id = "broken", When = null, Then = null! },
            TestClients.DefaultRule("healthy"),
        ];

        Assert.Equal("healthy", Evaluate(rules, TestClients.Client()).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_RuleWithBlankId_ReportsAnEmptyMatchedRuleIdRatherThanNull()
    {
        List<RoutingRule> rules = [new RoutingRule { Id = string.Empty, When = null, Then = TestClients.Web("https://a.example.com/") }];

        Assert.Equal(string.Empty, Evaluate(rules, TestClients.Client()).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_CopiesTheActionOntoTheDecision()
    {
        RuleAction action = new()
        {
            Action = RoutingActionKind.AppOrStore,
            Url = "https://web.example.com/",
            StoreUrl = "https://play.google.com/store/apps/details?id=sk.example",
            DeeplinkPath = "/product/123",
            ReferrerTemplate = "dl_cid={click_id}",
            Interstitial = InterstitialMode.Always,
        };
        List<RoutingRule> rules = [TestClients.Rule("all", null, action)];

        RoutingDecision decision = Evaluate(rules, TestClients.Client(Platform.Android));

        Assert.Equal(RoutingActionKind.AppOrStore, decision.Action);
        Assert.Equal("https://web.example.com/", decision.WebUrl);
        Assert.Equal("https://play.google.com/store/apps/details?id=sk.example", decision.StoreUrl);
        Assert.Equal("/product/123", decision.DeeplinkPath);
        Assert.Equal(InterstitialMode.Always, decision.Interstitial);
    }

    [Fact]
    public void Evaluate_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Engine.Evaluate([TestClients.DefaultRule()], null!, TestClients.FullConsent, ClickId));
    }

    [Fact]
    public void Evaluate_NullConsent_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Engine.Evaluate([TestClients.DefaultRule()], TestClients.Client(), null!, ClickId));
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RoutingEngine(null!));
    }

    // ---------------------------------------------------------------- click id and consent

    [Fact]
    public void Evaluate_WithoutClickIdLinkingConsent_StripsTheClickIdFromTheReferrerTemplate()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule(
                "android",
                null,
                new RuleAction
                {
                    Action = RoutingActionKind.AppOrStore,
                    StoreUrl = "https://play.google.com/store/apps/details?id=sk.example",
                    ReferrerTemplate = "dl_cid={click_id}&utm_source={utm_source}",
                }),
        ];

        RoutingDecision denied = Engine.Evaluate(rules, TestClients.Client(Platform.Android), TestClients.NoConsent, ClickId);
        RoutingDecision allowed = Engine.Evaluate(rules, TestClients.Client(Platform.Android), TestClients.FullConsent, ClickId);

        Assert.Equal("utm_source={utm_source}", denied.ReferrerTemplate);
        Assert.Equal("dl_cid={click_id}&utm_source={utm_source}", allowed.ReferrerTemplate);
    }

    [Fact]
    public void Evaluate_TemplateThatIsOnlyAClickId_BecomesNullWithoutConsent()
    {
        List<RoutingRule> rules =
        [
            TestClients.Rule(
                "android",
                null,
                new RuleAction
                {
                    Action = RoutingActionKind.AppOrStore,
                    StoreUrl = "https://play.google.com/store/apps/details?id=sk.example",
                    ReferrerTemplate = "dl_cid={click_id}",
                }),
        ];

        RoutingDecision decision = Engine.Evaluate(rules, TestClients.Client(Platform.Android), TestClients.NoConsent, ClickId);

        Assert.Null(decision.ReferrerTemplate);
    }

    // ---------------------------------------------------------------- clock

    [Fact]
    public void Evaluate_ClientWithoutATimestamp_UsesTheInjectedClockNotTheWallClock()
    {
        // Hour 12 on the frozen clock. A time window that only opens at hour 12 must match a client
        // whose ReceivedAt was never set, and must not match under a clock frozen at hour 03.
        RuleCondition noon = new() { TimeWindow = new TimeWindowPredicate { HoursUtc = [12] } };
        List<RoutingRule> rules =
        [
            TestClients.Rule("noon", noon, TestClients.Web("https://noon.example.com/")),
            TestClients.DefaultRule(),
        ];
        ClientContext untimed = ClientContext.Empty;

        RoutingDecision atNoon = new RoutingEngine(new FrozenClock(TestClients.Now))
            .Evaluate(rules, untimed, TestClients.FullConsent, ClickId);
        RoutingDecision atNight = new RoutingEngine(new FrozenClock(TestClients.Now.AddHours(-9)))
            .Evaluate(rules, untimed, TestClients.FullConsent, ClickId);

        Assert.Equal("noon", atNoon.MatchedRuleId);
        Assert.Equal("default", atNight.MatchedRuleId);
    }

    [Fact]
    public void Evaluate_ClientWithATimestamp_PrefersTheRequestArrivalTimeOverTheClock()
    {
        RuleCondition noon = new() { TimeWindow = new TimeWindowPredicate { HoursUtc = [12] } };
        List<RoutingRule> rules =
        [
            TestClients.Rule("noon", noon, TestClients.Web("https://noon.example.com/")),
            TestClients.DefaultRule(),
        ];

        // Clock says 03:30, the request says 12:30. The request wins so that a replayed decision and
        // a live decision for the same click agree.
        RoutingDecision decision = new RoutingEngine(new FrozenClock(TestClients.Now.AddHours(-9)))
            .Evaluate(rules, TestClients.Client(receivedAt: TestClients.Now), TestClients.FullConsent, ClickId);

        Assert.Equal("noon", decision.MatchedRuleId);
    }

    private static RoutingDecision Evaluate(IReadOnlyList<RoutingRule> rules, ClientContext client) =>
        Engine.Evaluate(rules, client, TestClients.FullConsent, ClickId);
}
