using System.Collections.ObjectModel;
using System.Globalization;
using CsCheck;
using Dle.Domain.Clients;
using Dle.Domain.Links;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.UnitTests.Links;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// Section D.4 asks for property based tests "mainly for the routing engine and URL normalisation".
/// Two laws matter operationally: the engine is total (a validated rule set always produces a
/// decision, so a click is never dropped) and the URL builder never emits something a browser will
/// refuse to follow.
/// </summary>
public sealed class RoutingPropertyTests
{
    private static readonly RoutingEngine Engine = new(new FrozenClock(TestClients.Now));

    private static readonly RoutingDecision WebDecision = new()
    {
        Kind = DecisionKind.Web,
        MatchedRuleId = "default",
        Action = RoutingActionKind.Web,
    };

    [Fact]
    public void Evaluate_AnyValidatedRuleSetAndAnyClient_AlwaysProducesADecision()
    {
        Gen.Select(ClientGen, ClickIdGen).Sample(pair =>
        {
            (ClientContext client, string clickId) = pair;
            IReadOnlyList<RoutingRule> rules = RuleSet();

            Assert.True(RoutingRuleValidator.IsValid(rules));

            RoutingDecision decision = Engine.Evaluate(rules, client, TestClients.FullConsent, clickId);

            Assert.NotEqual(DecisionKind.NotFound, decision.Kind);
            Assert.NotEqual(string.Empty, decision.MatchedRuleId);
        });
    }

    [Fact]
    public void Evaluate_IsDeterministicForOneClickId()
    {
        Gen.Select(ClientGen, ClickIdGen).Sample(pair =>
        {
            (ClientContext client, string clickId) = pair;
            IReadOnlyList<RoutingRule> rules = RuleSet();

            Assert.Equal(
                Engine.Evaluate(rules, client, TestClients.FullConsent, clickId),
                Engine.Evaluate(rules, client, TestClients.FullConsent, clickId));
        });
    }

    [Fact]
    public void Evaluate_ReportedAbBucket_IsAlwaysTheBucketOfTheClickId()
    {
        Gen.Select(ClientGen, ClickIdGen).Sample(pair =>
        {
            (ClientContext client, string clickId) = pair;

            RoutingDecision decision = Engine.Evaluate(RuleSet(), client, TestClients.FullConsent, clickId);

            if (decision.AbBucket is { } bucket)
            {
                Assert.Equal(ConsistentBucket.Of(clickId), bucket);
                Assert.NotNull(decision.AbVariant);
            }
            else
            {
                Assert.Null(decision.AbVariant);
            }
        });
    }

    [Fact]
    public void BuildWebUrl_AnyForwardableQueryValue_StillProducesAnAbsoluteUri()
    {
        Gen.Select(ForwardableKeyGen, Gen.String, Gen.Bool).Sample(triple =>
        {
            (string key, string value, bool withConsent) = triple;
            LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/product/123");
            ClientContext client = TestClients.Client(query: Single(key, value));
            ConsentDecision consent = withConsent ? TestClients.FullConsent : TestClients.NoConsent;

            string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, "01JQ8Z0K7M8T9RV", consent);

            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? built), $"'{url}' is not an absolute URI.");
            Assert.Equal("https", built!.Scheme);
            Assert.Equal("shop.example.com", built.Host);
            Assert.Equal("/product/123", built.AbsolutePath);
        });
    }

    [Fact]
    public void BuildWebUrl_ArbitraryQueryKey_NeverChangesTheOrigin()
    {
        Gen.Select(Gen.String, Gen.String).Sample(pair =>
        {
            (string key, string value) = pair;
            if (key.Length == 0)
            {
                return;
            }

            LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/product/123");
            ClientContext client = TestClients.Client(query: Single(key, value));

            string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, "01JQ8Z0K7M8T9RV", TestClients.NoConsent);

            Assert.StartsWith("https://shop.example.com/product/123", url, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void BuildStoreUrl_AnyUtmValue_KeepsThePlayStoreOrigin()
    {
        Gen.String.Sample(campaign =>
        {
            LinkSnapshot link = TestLinks.Snapshot(
                androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
                utm: TestLinks.Map(("utm_campaign", campaign)));

            string url = RoutingUrlBuilder.BuildStoreUrl(
                WebDecision, link, TestClients.Client(Platform.Android), "01JQ8Z0K7M8T9RV", TestClients.FullConsent);

            Assert.StartsWith("https://play.google.com/store/apps/details?id=sk.example", url, StringComparison.Ordinal);
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _), $"'{url}' is not an absolute URI.");
        });
    }

    [Fact]
    public void BuildDeeplinkUrl_AnyPath_ProducesEitherNullOrAUsableCustomSchemeUrl()
    {
        Gen.String.Sample(path =>
        {
            LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: path);

            string? url = RoutingUrlBuilder.BuildDeeplinkUrl(
                WebDecision, link, "myapp", "01JQ8Z0K7M8T9RV", TestClients.NoConsent);

            Assert.NotNull(url);
            Assert.StartsWith("myapp://", url, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- generators

    private static IReadOnlyList<RoutingRule> RuleSet() =>
    [
        new RoutingRule
        {
            Id = "ios-experiment",
            When = new RuleCondition
            {
                Platform = ["ios"],
                Ab = [new AbVariant { Variant = "a", Percent = 30 }, new AbVariant { Variant = "b", Percent = 30 }],
            },
            Then = TestClients.Web("https://ios.example.com/"),
        },
        new RoutingRule
        {
            Id = "android-sk",
            When = new RuleCondition { Platform = ["android"], Country = ["SK", "CZ"], Language = ["sk", "cs"] },
            Then = new RuleAction
            {
                Action = RoutingActionKind.AppOrStore,
                StoreUrl = "https://play.google.com/store/apps/details?id=sk.example",
            },
        },
        new RoutingRule
        {
            Id = "modern-os",
            When = new RuleCondition { OsVersion = new VersionPredicate { Gte = "18" } },
            Then = TestClients.Web("https://modern.example.com/"),
        },
        new RoutingRule
        {
            Id = "business-hours",
            When = new RuleCondition { TimeWindow = new TimeWindowPredicate { HoursUtc = [8, 9, 10, 11, 12] } },
            Then = TestClients.Web("https://hours.example.com/"),
        },
        TestClients.DefaultRule(),
    ];

    private static readonly Gen<ClientContext> ClientGen =
        Gen.Select(
                Gen.Enum<Platform>(),
                Gen.Enum<ClientChannel>(),
                Gen.OneOfConst<string?>(null, "SK", "CZ", "AT", "US"),
                Gen.OneOfConst<string?>(null, "sk", "sk-SK", "cs-CZ", "en-GB", "de"),
                Gen.OneOfConst<string?>(null, "17.0", "18", "18.1", "26.0", "not-a-version"),
                Gen.Int[0, 23])
            .Select(t => TestClients.Client(
                t.Item1,
                t.Item2,
                t.Item3,
                region: null,
                language: t.Item4,
                osVersion: t.Item5,
                receivedAt: new DateTimeOffset(2026, 3, 14, t.Item6, 0, 0, TimeSpan.Zero)));

    private static readonly Gen<string> ClickIdGen =
        Gen.Int[0, 100_000].Select(i => string.Create(CultureInfo.InvariantCulture, $"01JQ8Z0K7M8T9RV{i:D8}"));

    private static readonly Gen<string> ForwardableKeyGen =
        Gen.Int[0, RoutingUrlBuilder.ForwardableQueryKeys.Length - 1]
            .Select(i => RoutingUrlBuilder.ForwardableQueryKeys[i]);

    private static ReadOnlyDictionary<string, string> Single(string key, string value) =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value });
}
