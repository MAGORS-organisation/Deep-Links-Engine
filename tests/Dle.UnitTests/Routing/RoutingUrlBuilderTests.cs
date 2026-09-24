using System.Collections.ObjectModel;
using Dle.Domain.Clients;
using Dle.Domain.Links;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.UnitTests.Links;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// TC-164 and FR-128. The redirect target is never taken from the request: only the query component
/// of the configured target is ever touched, and only with keys from a fixed allowlist. Everything
/// else the caller sends is dropped on the floor.
/// </summary>
public sealed class RoutingUrlBuilderTests
{
    private const string ClickId = "01JQ8Z0K7M8T9RV";

    private static readonly RoutingDecision WebDecision = new()
    {
        Kind = DecisionKind.Web,
        MatchedRuleId = "default",
        Action = RoutingActionKind.Web,
    };

    // ================================================================ TC-164: open redirect

    [Theory]
    [Trait("TestCase", "TC-164")]
    [InlineData("to", "https://evil.example.com/")]
    [InlineData("url", "https://evil.example.com/")]
    [InlineData("redirect", "https://evil.example.com/")]
    [InlineData("redirect_uri", "https://evil.example.com/")]
    [InlineData("next", "//evil.example.com")]
    [InlineData("target", "javascript:alert(1)")]
    [InlineData("destination", "https://evil.example.com/")]
    [InlineData("continue", "https://evil.example.com/")]
    [InlineData("returnUrl", "https://evil.example.com/")]
    [InlineData("r", "https://evil.example.com/")]
    public void BuildWebUrl_HostileQueryParameter_NeverInfluencesTheTarget(string key, string value)
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/product/123");
        ClientContext client = TestClients.Client(query: Query((key, value)));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        AssertSameOrigin("https://shop.example.com/product/123", url);
        Assert.DoesNotContain("evil.example.com", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("TestCase", "TC-164")]
    public void BuildWebUrl_EveryHostileParameterAtOnce_LeavesSchemeHostAndPathUntouched()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/a/b/c");
        ClientContext client = TestClients.Client(query: Query(
            ("to", "https://evil.example.com"),
            ("url", "http://169.254.169.254/latest/meta-data/"),
            ("next", "//evil.example.com"),
            ("host", "evil.example.com"),
            ("scheme", "javascript"),
            ("path", "/../../etc/passwd"),
            ("dl_target", "https://evil.example.com")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        AssertSameOrigin("https://shop.example.com/a/b/c", url);
        Assert.DoesNotContain("evil", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("169.254", url, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "TC-164")]
    public void BuildStoreUrl_HostileQueryParameters_DoNotReachTheStoreUrl()
    {
        LinkSnapshot link = TestLinks.Snapshot(androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example");
        ClientContext client = TestClients.Client(Platform.Android, query: Query(("to", "https://evil.example.com")));

        string url = RoutingUrlBuilder.BuildStoreUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        Assert.StartsWith("https://play.google.com/store/apps/details?", url, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", url, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ TC-110: forwardable allowlist

    [Theory]
    [Trait("TestCase", "TC-110")]
    [InlineData("utm_source")]
    [InlineData("utm_medium")]
    [InlineData("utm_campaign")]
    [InlineData("utm_term")]
    [InlineData("utm_content")]
    [InlineData("gclid")]
    [InlineData("fbclid")]
    [InlineData("ttclid")]
    [InlineData("msclkid")]
    [InlineData("twclid")]
    [InlineData("li_fat_id")]
    [InlineData("igshid")]
    [InlineData("ref")]
    public void BuildWebUrl_AllowlistedParameter_IsForwardedToTheTarget(string key)
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");
        ClientContext client = TestClients.Client(query: Query((key, "carried")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        Assert.Contains(key + "=carried", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("password")]
    [InlineData("utm_sourcex")]
    [InlineData("x-utm_source")]
    public void BuildWebUrl_ParameterOutsideTheAllowlist_IsDropped(string key)
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");
        ClientContext client = TestClients.Client(query: Query((key, "leaked")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        Assert.DoesNotContain("leaked", url, StringComparison.Ordinal);
        Assert.DoesNotContain(key + "=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardableQueryKeys_IsExactlyTheDocumentedAllowlist()
    {
        string[] expected =
        [
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
            "gclid", "fbclid", "ttclid", "msclkid", "twclid", "li_fat_id", "igshid", "ref", "dl_cid",
        ];

        Assert.Equal(expected, RoutingUrlBuilder.ForwardableQueryKeys);
    }

    [Fact]
    public void BuildWebUrl_ClientSuppliedClickId_IsNotForwarded()
    {
        // dl_cid is on the allowlist so that it survives an internal hop, but a value the caller
        // supplies must never be mistaken for the click id this engine issued.
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");
        ClientContext client = TestClients.Client(query: Query(("dl_cid", "forged-click-id")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.FullConsent);

        Assert.DoesNotContain("forged-click-id", url, StringComparison.Ordinal);
        Assert.Contains("dl_cid=" + ClickId, url, StringComparison.Ordinal);
    }

    // ================================================================ UTM precedence

    [Fact]
    public void BuildWebUrl_LinkUtmDefaults_AreAppendedToTheTarget()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            "https://shop.example.com/p",
            utm: TestLinks.Map(("utm_source", "poster"), ("utm_medium", "print")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, TestClients.NoConsent);

        Assert.Contains("utm_source=poster", url, StringComparison.Ordinal);
        Assert.Contains("utm_medium=print", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_ParameterAlreadyOnTheTargetUrl_BeatsTheLinkUtmDefault()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            "https://shop.example.com/p?utm_source=onpage",
            utm: TestLinks.Map(("utm_source", "fromlink")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, TestClients.NoConsent);

        Assert.Contains("utm_source=onpage", url, StringComparison.Ordinal);
        Assert.DoesNotContain("fromlink", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_ParameterAlreadyOnTheTargetUrl_AlsoBeatsTheIncomingRequest()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p?utm_source=onpage");
        ClientContext client = TestClients.Client(query: Query(("utm_source", "fromrequest")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.NoConsent);

        Assert.Contains("utm_source=onpage", url, StringComparison.Ordinal);
        Assert.DoesNotContain("fromrequest", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_IncomingRequestParameter_BeatsTheStaticLinkUtmDefault()
    {
        // The request describes the traffic that actually arrived; the link default is a guess.
        LinkSnapshot link = TestLinks.Snapshot(
            "https://shop.example.com/p",
            utm: TestLinks.Map(("utm_source", "fromlink")));
        ClientContext client = TestClients.Client(query: Query(("utm_source", "fromrequest")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.NoConsent);

        Assert.Contains("utm_source=fromrequest", url, StringComparison.Ordinal);
        Assert.DoesNotContain("fromlink", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_TargetFragment_SurvivesAtTheEndOfTheUrl()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p#reviews");

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, TestClients.FullConsent);

        Assert.EndsWith("#reviews", url, StringComparison.Ordinal);
        Assert.Contains("dl_cid=" + ClickId, url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_QueryValuesAreEscaped()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");
        ClientContext client = TestClients.Client(query: Query(("utm_campaign", "spring sale&x=1")));

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, client, ClickId, TestClients.NoConsent);

        Assert.Contains("utm_campaign=spring%20sale%26x%3D1", url, StringComparison.Ordinal);
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _));
    }

    [Fact]
    public void BuildWebUrl_DecisionUrl_TakesPrecedenceOverTheLinkTarget()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/default");
        RoutingDecision decision = WebDecision with { WebUrl = "https://shop.example.com/rule-specific" };

        string url = RoutingUrlBuilder.BuildWebUrl(decision, link, TestClients.Client(), ClickId, TestClients.NoConsent);

        Assert.StartsWith("https://shop.example.com/rule-specific", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_DecisionUrlWithANonHttpScheme_IsIgnoredInFavourOfTheLinkTarget()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/default");
        RoutingDecision decision = WebDecision with { WebUrl = "javascript:alert(1)" };

        string url = RoutingUrlBuilder.BuildWebUrl(decision, link, TestClients.Client(), ClickId, TestClients.NoConsent);

        Assert.StartsWith("https://shop.example.com/default", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_NullArguments_Throw()
    {
        LinkSnapshot link = TestLinks.Snapshot();

        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildWebUrl(null!, link, TestClients.Client(), ClickId, TestClients.NoConsent));
        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildWebUrl(WebDecision, null!, TestClients.Client(), ClickId, TestClients.NoConsent));
        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildWebUrl(WebDecision, link, null!, ClickId, TestClients.NoConsent));
        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, null!));
    }

    // ================================================================ consent

    [Fact]
    [Trait("TestCase", "TC-146")]
    public void BuildWebUrl_WithoutClickIdLinkingConsent_AppendsNoClickId()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, TestClients.NoConsent);

        Assert.DoesNotContain("dl_cid", url, StringComparison.Ordinal);
        Assert.DoesNotContain(ClickId, url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_DefaultOverloadWithoutConsent_AppendsNoClickId()
    {
        // The three argument overload is the "consent not supplied" path; it must fail closed.
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId);

        Assert.DoesNotContain("dl_cid", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWebUrl_AggregateOnlyConsent_AppendsNoClickId()
    {
        ConsentDecision aggregate = ConsentGate.Evaluate(ConsentMode.AggregateOnly, null, new ConsentSignal { Analytics = true });
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p");

        string url = RoutingUrlBuilder.BuildWebUrl(WebDecision, link, TestClients.Client(), ClickId, aggregate);

        Assert.DoesNotContain("dl_cid", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_WithoutClickIdLinkingConsent_ShipsNoClickIdInThePlayReferrer()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(("utm_source", "poster")));

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.NoConsent);

        Assert.DoesNotContain("dl_cid", url, StringComparison.Ordinal);
        Assert.DoesNotContain(ClickId, url, StringComparison.Ordinal);
        Assert.Contains("referrer=utm_source%3Dposter", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDeeplinkUrl_WithoutClickIdLinkingConsent_AppendsNoClickId()
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "/product/123");

        string? url = RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, "myapp", ClickId, TestClients.NoConsent);

        Assert.Equal("myapp://product/123", url);
    }

    // ================================================================ Android install referrer

    [Fact]
    [Trait("TestCase", "TC-141")]
    public void BuildStoreUrl_Android_ExpandsTheReferrerTemplateAndEncodesItExactlyOnce()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(("utm_source", "poster"), ("utm_medium", "print"), ("utm_campaign", "spring")));

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent);

        string referrer = ReferrerOf(url);

        Assert.Equal(
            "dl_cid%3D" + ClickId + "%26utm_source%3Dposter%26utm_medium%3Dprint%26utm_campaign%3Dspring",
            referrer);
        Assert.DoesNotContain("%253D", referrer, StringComparison.Ordinal);
        Assert.DoesNotContain("%2526", referrer, StringComparison.Ordinal);
        Assert.Equal(
            "dl_cid=" + ClickId + "&utm_source=poster&utm_medium=print&utm_campaign=spring",
            Uri.UnescapeDataString(referrer));
    }

    [Fact]
    public void BuildStoreUrl_Android_DropsReferrerPairsWhoseUtmValueIsUnset()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(("utm_source", "poster")));

        string referrer = Uri.UnescapeDataString(ReferrerOf(RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent)));

        Assert.Equal("dl_cid=" + ClickId + "&utm_source=poster", referrer);
        Assert.DoesNotContain("utm_medium=", referrer, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_Android_UsesTheRuleReferrerTemplateWhenTheRuleSuppliesOne()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(("utm_campaign", "spring")));
        RoutingDecision decision = WebDecision with { ReferrerTemplate = "dl_cid={click_id}&c={utm_campaign}&l={link_id}" };

        string referrer = Uri.UnescapeDataString(ReferrerOf(RoutingUrlBuilder.BuildStoreUrl(
            decision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent)));

        Assert.Equal("dl_cid=" + ClickId + "&c=spring&l=42", referrer);
    }

    [Fact]
    public void BuildStoreUrl_Android_CapsTheEncodedReferrerAtFiveHundredCharacters()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(
                ("utm_source", "poster"),
                ("utm_medium", "print"),
                ("utm_campaign", new string('c', 600))));

        string referrer = ReferrerOf(RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent));

        Assert.True(
            referrer.Length <= RoutingUrlBuilder.MaxEncodedReferrerLength,
            $"The encoded referrer is {referrer.Length} characters, over the {RoutingUrlBuilder.MaxEncodedReferrerLength} character Play limit.");

        string decoded = Uri.UnescapeDataString(referrer);
        Assert.Contains("dl_cid=" + ClickId, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_campaign=", decoded, StringComparison.Ordinal);
        Assert.Contains("utm_source=poster", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_Android_KeepsTheClickIdEvenWhenEveryOtherPairMustGo()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example",
            utm: TestLinks.Map(
                ("utm_source", new string('s', 600)),
                ("utm_medium", new string('m', 600)),
                ("utm_campaign", new string('c', 600))));

        string decoded = Uri.UnescapeDataString(ReferrerOf(RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent)));

        Assert.Equal("dl_cid=" + ClickId, decoded);
    }

    [Fact]
    public void BuildStoreUrl_Android_PreservesTheStorePackageParameter()
    {
        LinkSnapshot link = TestLinks.Snapshot(androidStoreUrl: "https://play.google.com/store/apps/details?id=sk.example&hl=sk");

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Android), ClickId, TestClients.FullConsent);

        Assert.StartsWith("https://play.google.com/store/apps/details?id=sk.example&hl=sk&referrer=", url, StringComparison.Ordinal);
    }

    // ================================================================ iOS App Store

    [Fact]
    [Trait("TestCase", "TC-101")]
    public void BuildStoreUrl_Ios_AddsTheCampaignTokenFromTheLinkUtm()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            iosStoreUrl: "https://apps.apple.com/sk/app/id123456789",
            utm: TestLinks.Map(("utm_campaign", "spring")));

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.Equal("https://apps.apple.com/sk/app/id123456789?ct=spring", url);
    }

    [Fact]
    [Trait("TestCase", "TC-101")]
    public void BuildStoreUrl_Ios_KeepsExistingProviderCampaignAndMediaTokens()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            iosStoreUrl: "https://apps.apple.com/sk/app/id123456789?pt=987654&ct=handpicked&mt=8",
            utm: TestLinks.Map(("utm_campaign", "spring")));

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.Equal("https://apps.apple.com/sk/app/id123456789?pt=987654&ct=handpicked&mt=8", url);
    }

    [Fact]
    public void BuildStoreUrl_Ios_AddsTheCampaignTokenAlongsideProviderAndMediaTokens()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            iosStoreUrl: "https://apps.apple.com/sk/app/id123456789?pt=987654&mt=8",
            utm: TestLinks.Map(("utm_campaign", "spring")));

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.Equal("https://apps.apple.com/sk/app/id123456789?pt=987654&mt=8&ct=spring", url);
    }

    [Fact]
    public void BuildStoreUrl_Ios_NeverShipsAClickIdBecauseTheAppStoreHasNoChannelForIt()
    {
        LinkSnapshot link = TestLinks.Snapshot(iosStoreUrl: "https://apps.apple.com/sk/app/id123456789");

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.DoesNotContain(ClickId, url, StringComparison.Ordinal);
        Assert.DoesNotContain("referrer", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_DecisionStoreUrl_TakesPrecedenceOverThePlatformDefault()
    {
        LinkSnapshot link = TestLinks.Snapshot(iosStoreUrl: "https://apps.apple.com/sk/app/id111111111");
        RoutingDecision decision = WebDecision with { StoreUrl = "https://apps.apple.com/sk/app/id222222222" };

        string url = RoutingUrlBuilder.BuildStoreUrl(
            decision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.StartsWith("https://apps.apple.com/sk/app/id222222222", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_NoStoreUrlForThePlatform_FallsBackToTheWebTarget()
    {
        LinkSnapshot link = TestLinks.Snapshot("https://shop.example.com/p", androidStoreUrl: "https://play.google.com/x");

        string url = RoutingUrlBuilder.BuildStoreUrl(
            WebDecision, link, TestClients.Client(Platform.Ios), ClickId, TestClients.FullConsent);

        Assert.StartsWith("https://shop.example.com/p", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStoreUrl_DesktopClient_LeavesTheStoreUrlUndecorated()
    {
        LinkSnapshot link = TestLinks.Snapshot(utm: TestLinks.Map(("utm_campaign", "spring")));
        RoutingDecision decision = WebDecision with { StoreUrl = "https://apps.apple.com/sk/app/id123456789" };

        string url = RoutingUrlBuilder.BuildStoreUrl(
            decision, link, TestClients.Client(Platform.Desktop), ClickId, TestClients.FullConsent);

        Assert.Equal("https://apps.apple.com/sk/app/id123456789", url);
    }

    // ================================================================ deeplink URL

    [Fact]
    public void BuildDeeplinkUrl_CustomSchemeAndPath_ProducesTheSchemeUrl()
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "/product/123");

        string? url = RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, "myapp", ClickId, TestClients.FullConsent);

        Assert.Equal("myapp://product/123?dl_cid=" + ClickId, url);
    }

    [Fact]
    public void BuildDeeplinkUrl_DecisionPath_TakesPrecedenceOverTheLinkPath()
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "/from-link");
        RoutingDecision decision = WebDecision with { DeeplinkPath = "/from-rule" };

        string? url = RoutingUrlBuilder.BuildDeeplinkUrl(decision, link, "myapp", ClickId, TestClients.NoConsent);

        Assert.Equal("myapp://from-rule", url);
    }

    [Fact]
    public void BuildDeeplinkUrl_AbsoluteHttpsPath_IsUsedAsAUniversalLink()
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "https://dl.example.com/open/123");

        string? url = RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, "myapp", ClickId, TestClients.FullConsent);

        Assert.Equal("https://dl.example.com/open/123?dl_cid=" + ClickId, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("javascript")]
    [InlineData("data")]
    [InlineData("file")]
    [InlineData("intent")]
    [InlineData("blob")]
    [InlineData("about")]
    [InlineData("vbscript")]
    [InlineData("1app")]
    [InlineData("my app")]
    [InlineData("my/app")]
    public void BuildDeeplinkUrl_UnusableCustomScheme_ReturnsNull(string? scheme)
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "/product/123");

        Assert.Null(RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, scheme, ClickId, TestClients.FullConsent));
    }

    [Theory]
    [InlineData("myapp", "myapp://product/123")]
    [InlineData("my-app", "my-app://product/123")]
    [InlineData("my.app", "my.app://product/123")]
    [InlineData("my+app", "my+app://product/123")]
    [InlineData("myapp:", "myapp://product/123")]
    [InlineData("myapp://", "myapp://product/123")]
    public void BuildDeeplinkUrl_SchemeSpellings_AreNormalised(string scheme, string expected)
    {
        LinkSnapshot link = TestLinks.Snapshot(deeplinkPath: "/product/123");

        Assert.Equal(expected, RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, scheme, ClickId, TestClients.NoConsent));
    }

    [Fact]
    public void BuildDeeplinkUrl_NoPathAtAll_StillOpensTheApplicationRoot()
    {
        LinkSnapshot link = TestLinks.Snapshot();

        Assert.Equal("myapp://", RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, "myapp", ClickId, TestClients.NoConsent));
    }

    [Fact]
    public void BuildDeeplinkUrl_NullArguments_Throw()
    {
        LinkSnapshot link = TestLinks.Snapshot();

        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildDeeplinkUrl(null!, link, "myapp", ClickId, TestClients.NoConsent));
        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, null!, "myapp", ClickId, TestClients.NoConsent));
        Assert.Throws<ArgumentNullException>(() => RoutingUrlBuilder.BuildDeeplinkUrl(WebDecision, link, "myapp", ClickId, null!));
    }

    // ================================================================ helpers

    private static ReadOnlyDictionary<string, string> Query(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs)
        {
            map[key] = value;
        }

        return new ReadOnlyDictionary<string, string>(map);
    }

    private static void AssertSameOrigin(string expectedPrefix, string actual)
    {
        Assert.True(Uri.TryCreate(expectedPrefix, UriKind.Absolute, out Uri? expected));
        Assert.True(Uri.TryCreate(actual, UriKind.Absolute, out Uri? built), $"'{actual}' is not an absolute URI.");
        Assert.Equal(expected!.Scheme, built!.Scheme);
        Assert.Equal(expected.Host, built.Host);
        Assert.Equal(expected.AbsolutePath, built.AbsolutePath);
    }

    private static string ReferrerOf(string url)
    {
        int index = url.IndexOf("referrer=", StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{url}' carries no referrer parameter.");

        string tail = url[(index + "referrer=".Length)..];
        int end = tail.IndexOf('&');
        return end < 0 ? tail : tail[..end];
    }
}
