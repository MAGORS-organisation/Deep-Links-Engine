using System.Collections.ObjectModel;

using Dle.Domain.Clients;
using Dle.Domain.Links;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.Edge.Rendering;
using Dle.Edge.Resolution;

using Microsoft.AspNetCore.Http;

using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// The interstitial is the one page in the engine that renders attacker-influenced text: Open Graph
/// metadata comes from whoever created the link, and the target URL can carry whatever a campaign
/// put in it (T-11). Two defences have to hold together — every value is HTML encoded on the way
/// out, and the Content-Security-Policy admits no inline script (S-04) — because either one alone
/// fails to an author who controls the other.
/// </summary>
public sealed class InterstitialRenderingTests
{
    private const string HostileTitle = "<script>alert(1)</script>";

    private const string HostileDescription = "\" onmouseover=\"alert(2)\" x=\"";

    private static readonly DateTimeOffset Now = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Criterion", "S-04")]
    public async Task OgPreview_AllowsTheOriginOfItsOwnImage_AndNoOther()
    {
        LinkSnapshot link = Link(og: new OgMeta
        {
            Title = "Autumn",
            ImageUrl = "https://cdn.example/promo/image.png?v=2",
        });

        RenderedPage page = await RenderedPage.RenderAsync(
            InterstitialResults.OgPreview(link, Crawler(), domain: null, canonicalUrl: "https://go.example/abc"));

        // The preview is the one page that shows a picture from outside the deployment, and the
        // policy names where it comes from: the origin, not the path it was fetched by and not the
        // scheme it happens to use. A page that referenced no image would carry none of this.
        Assert.Contains("img-src 'self' https://cdn.example;", page.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("https:;", page.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("promo/image.png", page.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    public async Task AStatusPage_ReferencesNoImage_AndItsPolicySaysSo()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.NotFound(language: null));

        Assert.Contains("img-src 'self';", page.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "T-11")]
    public async Task OgPreview_HostileOpenGraphMetadata_IsEncoded()
    {
        LinkSnapshot link = Link(og: new OgMeta
        {
            Title = HostileTitle,
            Description = HostileDescription,
            ImageUrl = "https://cdn.example/image.png",
            SiteName = "<b>Example</b>",
        });

        RenderedPage page = await RenderedPage.RenderAsync(
            InterstitialResults.OgPreview(link, Crawler(), domain: null, canonicalUrl: "https://go.example/abc"));

        Assert.Equal(StatusCodes.Status200OK, page.Status);

        // The script element never reaches the document as markup.
        Assert.DoesNotContain(HostileTitle, page.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", page.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page.Html, StringComparison.Ordinal);

        // Neither does an attribute-breaking quote inside a meta content value.
        Assert.DoesNotContain("onmouseover=\"alert(2)\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("&quot;", page.Html, StringComparison.Ordinal);

        Assert.DoesNotContain("<b>Example</b>", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "T-11")]
    public async Task OgPreview_HostileMetadata_StillProducesTheOpenGraphTagsACrawlerNeeds()
    {
        LinkSnapshot link = Link(og: new OgMeta { Title = HostileTitle, Description = "Safe enough." });

        RenderedPage page = await RenderedPage.RenderAsync(
            InterstitialResults.OgPreview(link, Crawler(), domain: null, canonicalUrl: "https://go.example/abc"));

        // TC-106: the answer is a real Open Graph document, not an empty page.
        Assert.Contains("<meta property=\"og:title\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:description\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:url\" content=\"https://go.example/abc\">", page.Html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"twitter:card\"", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "T-11")]
    public async Task Interstitial_HostileQueryValueInTheTarget_IsEncodedInTheHref()
    {
        // A query value that survived into the outgoing URL. The anchor must not be able to escape
        // its own attribute.
        const string hostileTarget = "https://shop.example/p?q=%22%3E%3Cscript%3Ealert(3)%3C/script%3E&utm=1";

        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            hostileTarget,
            deeplinkUrl: null));

        Assert.DoesNotContain("<script>alert(3)", page.Html, StringComparison.Ordinal);
        Assert.Contains("&amp;utm=1", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interstitial_ContainsARealAnchorToTheTarget()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: "myapp://shop/summer"));

        // A person in an in-app browser has to be able to leave by tapping something. That "something"
        // has to be an anchor with a real href: a button wired up in script does not survive a strict
        // policy, and does not survive script being off at all.
        Assert.Contains("<a class=\"dle-btn\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://shop.example/summer\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"myapp://shop/summer\"", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interstitial_DeeplinkThatIsNotAnAllowedScheme_IsDropped()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: "javascript:alert(1)"));

        Assert.DoesNotContain("javascript:", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://shop.example/summer\"", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "S-04")]
    public async Task Interstitial_ContentSecurityPolicy_AdmitsNoInlineScript()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: null));

        string csp = page.ContentSecurityPolicy;

        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-hashes", csp, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'nonce-", csp, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "S-04")]
    public async Task Interstitial_EveryScriptElementCarriesTheNonceFromTheHeader()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: null));

        string nonce = NonceOf(page.ContentSecurityPolicy);

        Assert.False(string.IsNullOrEmpty(nonce));

        foreach (int index in ScriptElementOffsets(page.Html))
        {
            string element = page.Html[index..page.Html.IndexOf('>', index)];

            Assert.Contains("nonce=\"" + nonce + "\"", element, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("TestCase", "S-04")]
    public async Task Interstitial_NonceIsFreshOnEveryResponse()
    {
        static Task<RenderedPage> RenderOnce() => RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: null));

        RenderedPage first = await RenderOnce();
        RenderedPage second = await RenderOnce();

        // A reused nonce is the same as no nonce at all.
        Assert.NotEqual(NonceOf(first.ContentSecurityPolicy), NonceOf(second.ContentSecurityPolicy));
    }

    [Fact]
    public async Task Interstitial_CarriesTheHardeningHeadersAndIsNotCached()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Page(
            Link(),
            WebDecision(),
            MobileClient(),
            "https://shop.example/summer",
            deeplinkUrl: null));

        Assert.Equal("nosniff", page.Headers.XContentTypeOptions.ToString());
        Assert.Equal("DENY", page.Headers.XFrameOptions.ToString());
        Assert.Equal("no-referrer", page.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store", page.Headers.CacheControl.ToString());
    }

    [Fact]
    public void NonDocumentResponses_CarryAPolicyWithoutInlineScript()
    {
        string csp = SecurityHeadersMiddleware.NonDocumentContentSecurityPolicy;

        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("no-referrer", SecurityHeadersMiddleware.ReferrerPolicy);
    }

    /// <summary>
    /// TC-102 and TC-166. A slug that never existed and a slug whose link belongs to somebody else
    /// have to be indistinguishable, so the 404 document may not vary with anything about the link
    /// that was asked for.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-102")]
    public async Task NotFound_IsTheSameDocumentWhateverWasAskedFor()
    {
        RenderedPage missing = await RenderedPage.RenderAsync(InterstitialResults.NotFound(MobileClient()));
        RenderedPage foreign = await RenderedPage.RenderAsync(InterstitialResults.NotFound(MobileClient()));
        RenderedPage inactive = await RenderedPage.RenderAsync(InterstitialResults.NotFound(DesktopClient()));

        Assert.Equal(StatusCodes.Status404NotFound, missing.Status);

        // Identical apart from the per-response nonce, which is fixed length and independent of what
        // was asked for — so there is no content oracle and no length oracle either.
        Assert.Equal(missing.HtmlWithoutNonces, foreign.HtmlWithoutNonces);
        Assert.Equal(missing.HtmlWithoutNonces, inactive.HtmlWithoutNonces);
        Assert.Equal(missing.Body.Length, foreign.Body.Length);
        Assert.Equal(missing.Body.Length, inactive.Body.Length);
        Assert.Equal(missing.Headers.ContentLength, foreign.Headers.ContentLength);
    }

    [Fact]
    [Trait("TestCase", "TC-102")]
    public async Task NotFound_NamesNeitherTheSlugNorTheReason()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.NotFound(MobileClient()));

        foreach (string leak in new[] { "quarantine", "expired", "inactive", "tenant", "abc123" })
        {
            Assert.DoesNotContain(leak, page.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("TestCase", "TC-103")]
    public async Task Gone_IsAnExplainedPageRatherThanARedirect()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.Gone(MobileClient()));

        Assert.Equal(StatusCodes.Status410Gone, page.Status);
        Assert.Empty(page.Headers.Location.ToString());
        Assert.NotEmpty(page.Body);
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    public async Task TooManyRequests_CarriesRetryAfter()
    {
        RenderedPage page = await RenderedPage.RenderAsync(
            InterstitialResults.TooManyRequests(MobileClient(), domain: null, TimeSpan.FromMinutes(15)));

        Assert.Equal(StatusCodes.Status429TooManyRequests, page.Status);
        Assert.Equal("900", page.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Pages_AreServedAsUtf8Html()
    {
        RenderedPage page = await RenderedPage.RenderAsync(InterstitialResults.NotFound(MobileClient()));

        Assert.Equal(page.Body.Length, page.Headers.ContentLength);
        Assert.Contains("text/html", page.Headers.ContentType.ToString(), StringComparison.Ordinal);
        Assert.Contains("charset=utf-8", page.Headers.ContentType.ToString(), StringComparison.Ordinal);
    }

    private static IEnumerable<int> ScriptElementOffsets(string html)
    {
        int index = html.IndexOf("<script", StringComparison.Ordinal);

        while (index >= 0)
        {
            yield return index;
            index = html.IndexOf("<script", index + 1, StringComparison.Ordinal);
        }
    }

    private static string NonceOf(string csp)
    {
        const string marker = "script-src 'nonce-";

        int start = csp.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        int end = csp.IndexOf('\'', start);

        return end < 0 ? string.Empty : csp[start..end];
    }

    private static LinkSnapshot Link(OgMeta? og = null) => new()
    {
        Id = 1,
        TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DomainId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Slug = "abc123",
        TargetUrl = "https://shop.example/summer",
        RoutingRules = [],
        Og = og ?? OgMeta.Empty,
        Utm = ReadOnlyDictionary<string, string>.Empty,
        IsActive = true,
        TenantConsentMode = ConsentMode.Full,
        IosCustomScheme = "myapp",
        AndroidCustomScheme = "myapp",
    };

    private static RoutingDecision WebDecision() => new()
    {
        Kind = DecisionKind.Interstitial,
        MatchedRuleId = "default",
        Action = RoutingActionKind.Web,
        WebUrl = "https://shop.example/summer",
        Interstitial = InterstitialMode.Always,
    };

    private static ClientContext MobileClient() => new()
    {
        Platform = Platform.Ios,
        DeviceClass = DeviceClass.Phone,
        Channel = ClientChannel.InAppInstagram,
        Language = "en",
        ReceivedAt = Now,
    };

    private static ClientContext DesktopClient() => new()
    {
        Platform = Platform.Desktop,
        DeviceClass = DeviceClass.Desktop,
        Channel = ClientChannel.Browser,
        Language = "en",
        ReceivedAt = Now,
    };

    private static ClientContext Crawler() => new()
    {
        Platform = Platform.Other,
        DeviceClass = DeviceClass.Bot,
        Channel = ClientChannel.Crawler,
        IsCrawler = true,
        CrawlerName = "facebookexternalhit",
        ReceivedAt = Now,
    };

}
