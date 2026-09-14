using System.Globalization;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// TC-105 and FR-127. Validation runs at write time, so an invalid rule set never reaches the hot
/// path. The error Path strings are part of the API contract (RFC 9457 problem details point the
/// admin UI at one field), so they are asserted, not just the number of errors.
/// </summary>
public sealed class RoutingRuleValidatorTests
{
    private static readonly RuleAction WebAction = new()
    {
        Action = RoutingActionKind.Web,
        Url = "https://example.com/",
    };

    // ---------------------------------------------------------------- the default rule

    [Fact]
    [Trait("TestCase", "TC-105")]
    public void Validate_RuleSetWithoutADefaultRule_IsInvalid()
    {
        List<RoutingRule> rules =
        [
            Rule("ios", new RuleCondition { Platform = ["ios"] }),
            Rule("android", new RuleCondition { Platform = ["android"] }),
        ];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        Assert.False(RoutingRuleValidator.IsValid(rules));
        RoutingValidationError error = Assert.Single(errors);
        Assert.Equal("rules", error.Path);
        Assert.Contains("default rule", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("TestCase", "TC-105")]
    public void Validate_NullOrEmptyRuleSet_IsInvalid()
    {
        Assert.Equal("rules", Assert.Single(RoutingRuleValidator.Validate(null)).Path);
        Assert.Equal("rules", Assert.Single(RoutingRuleValidator.Validate([])).Path);
        Assert.False(RoutingRuleValidator.IsValid(null));
        Assert.False(RoutingRuleValidator.IsValid([]));
    }

    [Fact]
    public void Validate_MoreThanOneDefaultRule_IsInvalid()
    {
        List<RoutingRule> rules = [Rule("first", null), Rule("second", null)];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "rules" && e.Message.Contains("Exactly one default rule", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DefaultRuleThatIsNotLast_IsInvalid()
    {
        List<RoutingRule> rules =
        [
            Rule("catch-all", null),
            Rule("ios", new RuleCondition { Platform = ["ios"] }),
        ];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        RoutingValidationError error = Assert.Single(errors);
        Assert.Equal("rules[0]", error.Path);
        Assert.Contains("must be the last rule", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MinimalValidRuleSet_HasNoErrors()
    {
        List<RoutingRule> rules = [Rule("default", null)];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
        Assert.True(RoutingRuleValidator.IsValid(rules));
    }

    [Fact]
    public void Validate_RealisticRuleSet_HasNoErrors()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = "ios-app",
                When = new RuleCondition
                {
                    Platform = ["ios"],
                    OsVersion = new VersionPredicate { Gte = "16" },
                    Country = ["SK", "CZ"],
                    TimeWindow = new TimeWindowPredicate { HoursUtc = [8, 9, 10], DaysOfWeekUtc = [1, 2, 3, 4, 5] },
                    Ab = [new AbVariant { Variant = "a", Percent = 50 }, new AbVariant { Variant = "b", Percent = 50 }],
                },
                Then = new RuleAction
                {
                    Action = RoutingActionKind.AppOrStore,
                    StoreUrl = "https://apps.apple.com/sk/app/id123456789",
                    DeeplinkPath = "/product/123",
                    Interstitial = InterstitialMode.Auto,
                },
            },
            Rule("default", null),
        ];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    // ---------------------------------------------------------------- identifiers

    [Fact]
    public void Validate_DuplicateRuleIds_AreReportedOnTheSecondOccurrence()
    {
        List<RoutingRule> rules =
        [
            Rule("promo", new RuleCondition { Platform = ["ios"] }),
            Rule("promo", new RuleCondition { Platform = ["android"] }),
            Rule("default", null),
        ];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        RoutingValidationError error = Assert.Single(errors);
        Assert.Equal("rules[1].id", error.Path);
        Assert.Contains("Duplicate rule id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DuplicateIdsAreCaseSensitive()
    {
        List<RoutingRule> rules =
        [
            Rule("promo", new RuleCondition { Platform = ["ios"] }),
            Rule("PROMO", new RuleCondition { Platform = ["android"] }),
            Rule("default", null),
        ];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankRuleId_IsInvalid(string id)
    {
        List<RoutingRule> rules = [Rule(id, new RuleCondition { Platform = ["ios"] }), Rule("default", null)];

        Assert.Equal("rules[0].id", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_NullRule_IsReportedAtItsIndex()
    {
        List<RoutingRule> rules = [null!, Rule("default", null)];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "rules[0]" && e.Message.Contains("null", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- actions and URLs

    [Fact]
    public void Validate_WebActionWithoutAUrl_IsInvalid()
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = RoutingActionKind.Web } }];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].then.url", error.Path);
    }

    [Theory]
    [InlineData(RoutingActionKind.AppOrStore)]
    [InlineData(RoutingActionKind.StoreOnly)]
    public void Validate_StoreActionWithoutAStoreUrl_IsInvalid(RoutingActionKind action)
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = action } }];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].then.store_url", error.Path);
    }

    [Theory]
    [InlineData(RoutingActionKind.AppOnly)]
    [InlineData(RoutingActionKind.Block)]
    public void Validate_ActionThatNeedsNoUrl_IsValidWithoutOne(RoutingActionKind action)
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = action } }];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example.com/a")]
    [InlineData("myapp://product/123")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [Trait("TestCase", "TC-161")]
    public void Validate_NonHttpScheme_IsRejectedOnTheWebUrl(string url)
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = RoutingActionKind.Web, Url = url } }];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "rules[0].then.url");
    }

    [Fact]
    [Trait("TestCase", "TC-161")]
    public void Validate_NonHttpScheme_IsRejectedOnTheStoreUrlToo()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = "default",
                When = null,
                Then = new RuleAction { Action = RoutingActionKind.StoreOnly, StoreUrl = "market://details?id=sk.example" },
            },
        ];

        Assert.Contains(RoutingRuleValidator.Validate(rules), e => e.Path == "rules[0].then.store_url");
    }

    [Fact]
    public void Validate_RelativeUrl_IsRejectedAsNotAbsolute()
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = RoutingActionKind.Web, Url = "/relative/path" } }];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].then.url", error.Path);
        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.example.com/x")]
    [InlineData("//evil.example.com/x")]
    [InlineData("\\\\evil.example.com\\x")]
    public void Validate_DeeplinkPathThatIsReallyAnAbsoluteUrl_IsRejected(string deeplinkPath)
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = "default",
                When = null,
                Then = new RuleAction { Action = RoutingActionKind.AppOnly, DeeplinkPath = deeplinkPath },
            },
        ];

        Assert.Equal("rules[0].then.deeplink_path", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_OrdinaryDeeplinkPath_IsAccepted()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = "default",
                When = null,
                Then = new RuleAction { Action = RoutingActionKind.AppOnly, DeeplinkPath = "/product/123?variant=red" },
            },
        ];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Fact]
    public void Validate_MissingAction_IsReportedOnThen()
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = null! }];

        Assert.Equal("rules[0].then", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_UndefinedActionValue_IsReported()
    {
        List<RoutingRule> rules = [new RoutingRule { Id = "default", When = null, Then = new RuleAction { Action = (RoutingActionKind)77 } }];

        Assert.Equal("rules[0].then.action", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_UndefinedInterstitialMode_IsReported()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = "default",
                When = null,
                Then = new RuleAction { Action = RoutingActionKind.AppOnly, Interstitial = (InterstitialMode)77 },
            },
        ];

        Assert.Equal("rules[0].then.interstitial", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    // ---------------------------------------------------------------- A/B

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_VariantPercentOutsideOneToOneHundred_IsInvalid(int percent)
    {
        List<RoutingRule> rules =
        [
            Split("experiment", new AbVariant { Variant = "a", Percent = percent }),
            Rule("default", null),
        ];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].when.ab[0].percent", error.Path);
        Assert.Contains("between 1 and 100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_VariantPercentagesSummingAboveOneHundred_IsInvalid()
    {
        List<RoutingRule> rules =
        [
            Split(
                "experiment",
                new AbVariant { Variant = "a", Percent = 60 },
                new AbVariant { Variant = "b", Percent = 60 }),
            Rule("default", null),
        ];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].when.ab", error.Path);
        Assert.Contains("must not exceed 100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_VariantPercentagesSummingBelowOneHundred_IsAllowed()
    {
        List<RoutingRule> rules =
        [
            Split(
                "experiment",
                new AbVariant { Variant = "a", Percent = 10 },
                new AbVariant { Variant = "b", Percent = 10 }),
            Rule("default", null),
        ];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Fact]
    public void Validate_DuplicateVariantNames_AreInvalid()
    {
        List<RoutingRule> rules =
        [
            Split(
                "experiment",
                new AbVariant { Variant = "a", Percent = 40 },
                new AbVariant { Variant = "A", Percent = 40 }),
            Rule("default", null),
        ];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].when.ab[1].variant", error.Path);
        Assert.Contains("Duplicate variant name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_BlankVariantName_IsInvalid()
    {
        List<RoutingRule> rules = [Split("experiment", new AbVariant { Variant = "  ", Percent = 40 }), Rule("default", null)];

        Assert.Equal("rules[0].when.ab[0].variant", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_NullVariant_IsInvalid()
    {
        AbVariant[] variants = [null!];
        List<RoutingRule> rules = [Split("experiment", variants), Rule("default", null)];

        Assert.Equal("rules[0].when.ab[0]", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    // ---------------------------------------------------------------- time windows

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(99)]
    public void Validate_HourOutsideZeroToTwentyThree_IsInvalid(int hour)
    {
        List<RoutingRule> rules =
        [
            Window("campaign", new TimeWindowPredicate { HoursUtc = [hour] }),
            Rule("default", null),
        ];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].when.time_window.hours_utc[0]", error.Path);
        Assert.Contains("between 0 and 23", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Validate_DayOutsideZeroToSix_IsInvalid(int day)
    {
        List<RoutingRule> rules =
        [
            Window("campaign", new TimeWindowPredicate { DaysOfWeekUtc = [0, day] }),
            Rule("default", null),
        ];

        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));

        Assert.Equal("rules[0].when.time_window.days_of_week_utc[1]", error.Path);
        Assert.Contains("between 0 (Sunday) and 6 (Saturday)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_HoursAndDaysAtTheBoundaries_AreAccepted()
    {
        List<RoutingRule> rules =
        [
            Window("campaign", new TimeWindowPredicate { HoursUtc = [0, 23], DaysOfWeekUtc = [0, 6] }),
            Rule("default", null),
        ];

        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Fact]
    public void Validate_WindowEndingBeforeItStarts_IsInvalid()
    {
        DateTimeOffset from = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        List<RoutingRule> rules =
        [
            Window("campaign", new TimeWindowPredicate { From = from, To = from.AddSeconds(-1) }),
            Rule("default", null),
        ];

        Assert.Equal("rules[0].when.time_window.to", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    [Fact]
    public void Validate_EmptyWindow_IsInvalid()
    {
        DateTimeOffset from = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        List<RoutingRule> rules = [Window("campaign", new TimeWindowPredicate { From = from, To = from }), Rule("default", null)];

        Assert.Equal("rules[0].when.time_window.to", Assert.Single(RoutingRuleValidator.Validate(rules)).Path);
    }

    // ---------------------------------------------------------------- size limits

    [Fact]
    public void Validate_MoreThanMaxRules_IsInvalid()
    {
        List<RoutingRule> rules = [];
        for (int i = 0; i < RoutingRuleValidator.MaxRules; i++)
        {
            rules.Add(Rule(string.Create(CultureInfo.InvariantCulture, $"r{i}"), new RuleCondition { Platform = ["ios"] }));
        }

        rules.Add(Rule("default", null));

        Assert.Equal(RoutingRuleValidator.MaxRules + 1, rules.Count);
        RoutingValidationError error = Assert.Single(RoutingRuleValidator.Validate(rules));
        Assert.Equal("rules", error.Path);
        Assert.Contains("at most 50 rules", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ExactlyMaxRules_IsValid()
    {
        List<RoutingRule> rules = [];
        for (int i = 0; i < RoutingRuleValidator.MaxRules - 1; i++)
        {
            rules.Add(Rule(string.Create(CultureInfo.InvariantCulture, $"r{i}"), new RuleCondition { Platform = ["ios"] }));
        }

        rules.Add(Rule("default", null));

        Assert.Equal(RoutingRuleValidator.MaxRules, rules.Count);
        Assert.Empty(RoutingRuleValidator.Validate(rules));
    }

    [Fact]
    public void Constants_MatchTheBindingContract()
    {
        Assert.Equal(50, RoutingRuleValidator.MaxRules);
        Assert.Equal(64 * 1024, RoutingRuleValidator.MaxJsonBytes);
    }

    // ---------------------------------------------------------------- several problems at once

    [Fact]
    public void Validate_SeveralProblems_ReportsEveryOneOfThem()
    {
        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Id = string.Empty,
                When = new RuleCondition { Ab = [new AbVariant { Variant = "a", Percent = 0 }] },
                Then = new RuleAction { Action = RoutingActionKind.Web, Url = "javascript:alert(1)" },
            },
        ];

        IReadOnlyList<RoutingValidationError> errors = RoutingRuleValidator.Validate(rules);

        Assert.Contains(errors, e => e.Path == "rules[0].id");
        Assert.Contains(errors, e => e.Path == "rules[0].when.ab[0].percent");
        Assert.Contains(errors, e => e.Path == "rules[0].then.url");
        Assert.Contains(errors, e => e.Path == "rules");
    }

    private static RoutingRule Rule(string id, RuleCondition? when) => new() { Id = id, When = when, Then = WebAction };

    private static RoutingRule Split(string id, params AbVariant[] variants) =>
        new() { Id = id, When = new RuleCondition { Ab = variants }, Then = WebAction };

    private static RoutingRule Window(string id, TimeWindowPredicate window) =>
        new() { Id = id, When = new RuleCondition { TimeWindow = window }, Then = WebAction };
}
