using System.Globalization;
using Dle.Domain.Clients;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-125. The A/B split is a condition like any other: a rule whose cumulative percentage does not
/// reach the click's bucket simply does not match, and evaluation continues. That is the part that
/// is easy to get wrong — an implementation that "falls back to the first variant" silently turns a
/// 10 % experiment into a 100 % rollout.
/// </summary>
public sealed class RoutingEngineAbTests
{
    private static readonly RoutingEngine Engine = new(new FrozenClock(TestClients.Now));

    [Fact]
    public void Evaluate_SameClickId_AlwaysPicksTheSameVariant()
    {
        List<RoutingRule> rules = [SplitRule("split", ("a", 50), ("b", 50)), TestClients.DefaultRule()];
        string clickId = TestClients.ClickIdWithBucket(7);

        RoutingDecision first = Evaluate(rules, clickId);
        for (int i = 0; i < 25; i++)
        {
            RoutingDecision again = Evaluate(rules, clickId);
            Assert.Equal(first.AbVariant, again.AbVariant);
            Assert.Equal(first.AbBucket, again.AbBucket);
        }
    }

    [Fact]
    public void Evaluate_BucketBelowTheFirstVariantShare_SelectsTheFirstVariant()
    {
        List<RoutingRule> rules = [SplitRule("split", ("a", 30), ("b", 70)), TestClients.DefaultRule()];

        RoutingDecision decision = Evaluate(rules, TestClients.ClickIdWithBucket(0));

        Assert.Equal("split", decision.MatchedRuleId);
        Assert.Equal("a", decision.AbVariant);
        Assert.Equal((short)0, decision.AbBucket);
    }

    [Fact]
    public void Evaluate_BucketAtTheVariantBoundary_SelectsTheNextVariant()
    {
        List<RoutingRule> rules = [SplitRule("split", ("a", 30), ("b", 70)), TestClients.DefaultRule()];

        Assert.Equal("a", Evaluate(rules, TestClients.ClickIdWithBucket(29)).AbVariant);
        Assert.Equal("b", Evaluate(rules, TestClients.ClickIdWithBucket(30)).AbVariant);
        Assert.Equal("b", Evaluate(rules, TestClients.ClickIdWithBucket(99)).AbVariant);
    }

    [Fact]
    public void Evaluate_BucketBeyondTheCumulativePercentage_DoesNotMatchAndEvaluationContinues()
    {
        List<RoutingRule> rules =
        [
            SplitRule("experiment", ("a", 10)),
            TestClients.Rule("runner-up", null, TestClients.Web("https://runner-up.example.com/")),
        ];

        RoutingDecision inside = Evaluate(rules, TestClients.ClickIdWithBucket(9));
        RoutingDecision outside = Evaluate(rules, TestClients.ClickIdWithBucket(10));

        Assert.Equal("experiment", inside.MatchedRuleId);
        Assert.Equal("a", inside.AbVariant);

        Assert.Equal("runner-up", outside.MatchedRuleId);
        Assert.Null(outside.AbVariant);
        Assert.Null(outside.AbBucket);
    }

    [Fact]
    public void Evaluate_PartialSplit_LeavesTheRemainderToTheDefaultRule()
    {
        List<RoutingRule> rules = [SplitRule("experiment", ("a", 20), ("b", 20)), TestClients.DefaultRule()];

        Assert.Equal("experiment", Evaluate(rules, TestClients.ClickIdWithBucket(39)).MatchedRuleId);
        Assert.Equal("default", Evaluate(rules, TestClients.ClickIdWithBucket(40)).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_TwoRulesWithSplits_ShareOneBucketForTheWholeRequest()
    {
        // If the bucket were recomputed per rule, a client could slide between experiments as it
        // walks down the list.
        List<RoutingRule> rules =
        [
            SplitRule("narrow", ("x", 30)),
            SplitRule("wide", ("y", 80)),
            TestClients.DefaultRule(),
        ];
        string clickId = TestClients.ClickIdWithBucket(50);

        RoutingDecision decision = Evaluate(rules, clickId);

        Assert.Equal("wide", decision.MatchedRuleId);
        Assert.Equal("y", decision.AbVariant);
        Assert.Equal((short)50, decision.AbBucket);
    }

    [Fact]
    public void Evaluate_RuleWithoutASplit_ReportsNoVariantAndNoBucket()
    {
        List<RoutingRule> rules = [TestClients.DefaultRule()];

        RoutingDecision decision = Evaluate(rules, "clk_plain");

        Assert.Null(decision.AbVariant);
        Assert.Null(decision.AbBucket);
    }

    [Fact]
    public void Evaluate_SplitCombinedWithAnotherCondition_AppliesBoth()
    {
        RoutingRule rule = new()
        {
            Id = "ios-experiment",
            When = new RuleCondition { Platform = ["ios"], Ab = [new AbVariant { Variant = "a", Percent = 100 }] },
            Then = TestClients.Web("https://experiment.example.com/"),
        };
        List<RoutingRule> rules = [rule, TestClients.DefaultRule()];
        string clickId = TestClients.ClickIdWithBucket(3);

        Assert.Equal("ios-experiment", Engine.Evaluate(rules, TestClients.Client(Platform.Ios), TestClients.FullConsent, clickId).MatchedRuleId);
        Assert.Equal("default", Engine.Evaluate(rules, TestClients.Client(Platform.Android), TestClients.FullConsent, clickId).MatchedRuleId);
    }

    [Fact]
    public void Evaluate_VariantWithZeroOrNegativePercent_IsSkipped()
    {
        RoutingRule rule = new()
        {
            Id = "split",
            When = new RuleCondition
            {
                Ab =
                [
                    new AbVariant { Variant = "dead", Percent = 0 },
                    new AbVariant { Variant = "live", Percent = 100 },
                ],
            },
            Then = TestClients.Web("https://experiment.example.com/"),
        };

        Assert.Equal("live", Evaluate([rule, TestClients.DefaultRule()], TestClients.ClickIdWithBucket(0)).AbVariant);
    }

    [Fact]
    public void Evaluate_FullPopulationAcrossManyClicks_SplitsCloseToTheConfiguredShare()
    {
        List<RoutingRule> rules = [SplitRule("split", ("a", 25), ("b", 75)), TestClients.DefaultRule()];
        int a = 0;
        int b = 0;

        for (int i = 0; i < 20_000; i++)
        {
            string? variant = Evaluate(rules, string.Create(CultureInfo.InvariantCulture, $"01JQ8Z0K7M8T9RV{i:D8}")).AbVariant;
            if (string.Equals(variant, "a", StringComparison.Ordinal))
            {
                a++;
            }
            else if (string.Equals(variant, "b", StringComparison.Ordinal))
            {
                b++;
            }
        }

        Assert.Equal(20_000, a + b);
        Assert.InRange(a, 4_400, 5_600);
        Assert.InRange(b, 14_400, 15_600);
    }

    private static RoutingRule SplitRule(string id, params (string Variant, int Percent)[] variants)
    {
        AbVariant[] ab = new AbVariant[variants.Length];
        for (int i = 0; i < variants.Length; i++)
        {
            ab[i] = new AbVariant { Variant = variants[i].Variant, Percent = variants[i].Percent };
        }

        return new RoutingRule
        {
            Id = id,
            When = new RuleCondition { Ab = ab },
            Then = TestClients.Web("https://experiment.example.com/"),
        };
    }

    private static RoutingDecision Evaluate(IReadOnlyList<RoutingRule> rules, string clickId) =>
        Engine.Evaluate(rules, TestClients.Client(), TestClients.FullConsent, clickId);
}
