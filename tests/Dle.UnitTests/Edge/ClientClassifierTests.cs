using System.Collections.ObjectModel;
using System.Net;

using Dle.Domain.Clients;
using Dle.Domain.Routing;
using Dle.Edge.Clients;
using Dle.Edge.Configuration;

using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// Classification is the input to every routing rule and to every row of the click stream, so a
/// misread user agent is a wrong redirect and a wrong report at the same time (§B.6.1 step 3). The
/// table below is real traffic: the in-app browsers of the nine networks the interstitial exists
/// for, ordinary browsers on every form factor, and the crawler set of §D.2.
/// </summary>
public sealed class ClientClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One instance for the whole class. Building a classifier compiles the whole user agent
    /// regular expression cascade, which costs about a second; the type is immutable and documented
    /// as safe to share, so a fresh one per test case would buy nothing but a minute of CI time.
    /// </summary>
    private static readonly ClientClassifier Shared =
        new(NullGeoIpResolver.Instance, Options.Create(new EdgeOptions()));

    private static ClientClassifier Classifier() => Shared;

    /// <summary>
    /// In-app webviews. Each row is a user agent string as the network actually sends it, and the
    /// channel it must be reported as — the channel is what a routing rule matches on and what the
    /// click stream records.
    /// </summary>
    public static TheoryData<string, string> InAppWebViews() => new()
    {
        // Facebook, iOS and Android.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21F90 [FBAN/FBIOS;FBAV/468.0.0.35.107;FBBV/598923118;FBDV/iPhone15,2;FBMD/iPhone;FBSN/iOS;FBSV/17.5]",
            ChannelNames.InAppFacebook
        },
        {
            "Mozilla/5.0 (Linux; Android 14; SM-S918B Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/124.0.6367.179 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/462.0.0.48.85;]",
            ChannelNames.InAppFacebook
        },

        // Instagram, iOS and Android.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21E236 Instagram 333.0.0.25.91 (iPhone14,5; iOS 17_4; en_US; en; scale=3.00; 1170x2532; 585072164)",
            ChannelNames.InAppInstagram
        },
        {
            "Mozilla/5.0 (Linux; Android 13; Pixel 7 Build/TQ3A.230901.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/117.0.0.0 Mobile Safari/537.36 Instagram 302.0.0.23.114 Android",
            ChannelNames.InAppInstagram
        },

        // TikTok, both the current token and the historical one.
        {
            "Mozilla/5.0 (Linux; Android 12; SM-A525F Build/SP1A.210812.016; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 BytedanceWebview/d8a21c6 " +
            "trill_2022803040 JsSdk/1.0 NetType/WIFI Channel/googleplay",
            ChannelNames.InAppTikTok
        },
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/15E148 musical_ly_2022600030 JsSdk/2.0 NetType/WIFI",
            ChannelNames.InAppTikTok
        },

        // LinkedIn — note that the in-app token is LinkedInApp, not the LinkedInBot crawler token.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21B74 [LinkedInApp]/9.28.3785",
            ChannelNames.InAppLinkedIn
        },

        // Snapchat.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21D50 Snapchat/12.79.0.42 (like Safari/8617.1.17.10.9)",
            ChannelNames.InAppSnapchat
        },

        // X, formerly Twitter — Android and iOS spell it differently.
        {
            "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A.240505.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/125.0.6422.72 Mobile Safari/537.36 TwitterAndroid",
            ChannelNames.InAppTwitter
        },
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21F90 Twitter for iPhone/10.51",
            ChannelNames.InAppTwitter
        },

        // WhatsApp's in-app browser. It carries Mozilla, which is what separates it from the link
        // preview fetcher of the same name.
        {
            "Mozilla/5.0 (Linux; Android 13; SM-G991B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/120.0.6099.144 Mobile Safari/537.36 WhatsApp/2.24.6.78 A",
            ChannelNames.InAppWhatsApp
        },

        // Telegram's iOS webview. TelegramBot is a different token and a different answer.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/15E148 Telegram-iOS/10.9.2",
            ChannelNames.InAppTelegram
        },

        // Pinterest.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/21C62 [Pinterest/iOS]",
            ChannelNames.InAppPinterest
        },

        // Networks without a channel of their own still have to be recognised as webviews, because
        // the interstitial exists for exactly that case.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/15E148 Line/13.5.0",
            ChannelNames.InAppGeneric
        },
        {
            "Mozilla/5.0 (Linux; Android 13; SM-A536B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/118.0.0.0 Mobile Safari/537.36 KAKAOTALK 10.3.5",
            ChannelNames.InAppGeneric
        },
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Mobile/15E148 MicroMessenger/8.0.44(0x18002c2f) NetType/WIFI Language/en",
            ChannelNames.InAppGeneric
        },

        // An unbranded Android WebView: the "; wv)" token is the only signal there is.
        {
            "Mozilla/5.0 (Linux; Android 14; Pixel 6 Build/UQ1A.240205.004; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/121.0.6167.143 Mobile Safari/537.36",
            ChannelNames.InAppGeneric
        },

        // An unbranded iOS WKWebView: mobile WebKit with no Safari token.
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/21E219",
            ChannelNames.InAppGeneric
        },
    };

    /// <summary>Ordinary browsers, one per platform and form factor.</summary>
    public static TheoryData<string, Platform, DeviceClass> Browsers() => new()
    {
        {
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Version/17.5 Mobile/15E148 Safari/604.1",
            Platform.Ios,
            DeviceClass.Phone
        },
        {
            "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Version/17.5 Mobile/15E148 Safari/604.1",
            Platform.Ios,
            DeviceClass.Tablet
        },
        {
            "Mozilla/5.0 (Linux; Android 14; Pixel 8 Pro) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/125.0.6422.72 Mobile Safari/537.36",
            Platform.Android,
            DeviceClass.Phone
        },
        {
            "Mozilla/5.0 (Linux; Android 13; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/119.0.0.0 Safari/537.36",
            Platform.Android,
            DeviceClass.Tablet
        },
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/125.0.0.0 Safari/537.36",
            Platform.Desktop,
            DeviceClass.Desktop
        },
        {
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Version/17.4.1 Safari/605.1.15",
            Platform.Desktop,
            DeviceClass.Desktop
        },
        {
            "Mozilla/5.0 (X11; Linux x86_64; rv:126.0) Gecko/20100101 Firefox/126.0",
            Platform.Desktop,
            DeviceClass.Desktop
        },
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36 Edg/124.0.2478.67",
            Platform.Desktop,
            DeviceClass.Desktop
        },
    };

    /// <summary>The crawler set of §D.2: every one of these must be answered with an OG document.</summary>
    public static TheoryData<string, string> Crawlers() => new()
    {
        { "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", CrawlerCatalog.Facebook },
        { "facebookcatalog/1.0", CrawlerCatalog.Facebook },
        { "Twitterbot/1.0", CrawlerCatalog.Twitter },
        { "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)", CrawlerCatalog.Slack },
        { "Slack-ImgProxy 0.19 (+https://api.slack.com/robots)", CrawlerCatalog.Slack },
        { "LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)", CrawlerCatalog.LinkedIn },
        { "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", CrawlerCatalog.Discord },
        { "WhatsApp/2.23.20.0 A", CrawlerCatalog.WhatsApp },
        { "TelegramBot (like TwitterBot)", CrawlerCatalog.Telegram },
        { "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", CrawlerCatalog.Google },
        { "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", CrawlerCatalog.Bing },
        { "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
          "Version/13.1.1 Safari/605.1.15 (Applebot/0.1; +http://www.apple.com/go/applebot)", CrawlerCatalog.Apple },
    };

    [Theory]
    [MemberData(nameof(InAppWebViews))]
    public void Classify_InAppWebView_ReportsTheNetworkChannel(string userAgent, string expectedChannel)
    {
        ClientContext context = Classifier().Classify(Request(userAgent));

        Assert.Equal(expectedChannel, ChannelNames.From(context.Channel));
        Assert.False(context.IsCrawler);
        Assert.Null(context.CrawlerName);
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public void Classify_OrdinaryBrowser_ReportsPlatformAndFormFactor(
        string userAgent,
        Platform expectedPlatform,
        DeviceClass expectedDeviceClass)
    {
        ClientContext context = Classifier().Classify(Request(userAgent));

        Assert.Equal(expectedPlatform, context.Platform);
        Assert.Equal(expectedDeviceClass, context.DeviceClass);
        Assert.Equal(ClientChannel.Browser, context.Channel);
        Assert.False(context.IsCrawler);
    }

    [Theory]
    [MemberData(nameof(Crawlers))]
    [Trait("TestCase", "TC-106")]
    public void Classify_Crawler_IsDetectedAndNamed(string userAgent, string expectedName)
    {
        ClientContext context = Classifier().Classify(Request(userAgent));

        Assert.True(context.IsCrawler);
        Assert.Equal(expectedName, context.CrawlerName);
        Assert.Equal(ClientChannel.Crawler, context.Channel);
        Assert.Equal(ChannelNames.Crawler, ChannelNames.From(context.Channel));
        Assert.Equal(DeviceClass.Bot, context.DeviceClass);
        Assert.False(context.IsSpoofedBot);
    }

    [Fact]
    public void Classify_WhatsAppFetcherAndWhatsAppWebView_AreToldApart()
    {
        ClientClassifier classifier = Classifier();

        ClientContext fetcher = classifier.Classify(Request("WhatsApp/2.23.20.0 A"));

        ClientContext webView = classifier.Classify(Request(
            "Mozilla/5.0 (Linux; Android 13; SM-G991B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Version/4.0 Chrome/120.0.6099.144 Mobile Safari/537.36 WhatsApp/2.24.6.78 A"));

        // One wants an Open Graph document, the other is a person who must reach the application.
        Assert.True(fetcher.IsCrawler);
        Assert.False(webView.IsCrawler);
        Assert.Equal(ClientChannel.InAppWhatsApp, webView.Channel);
    }

    [Fact]
    public void Classify_DleSdk_IsANativeApp()
    {
        ClientContext context = Classifier().Classify(Request("DleSdk/1.4.2 (Android 14; Pixel 8)"));

        Assert.Equal(ClientChannel.NativeApp, context.Channel);
        Assert.Equal(ChannelNames.NativeApp, ChannelNames.From(context.Channel));
        Assert.Equal("1.4.2", context.AppVersion);
    }

    [Fact]
    public void Classify_NoUserAgent_IsUnknownRatherThanAnError()
    {
        ClientContext context = Classifier().Classify(Request(userAgent: null));

        Assert.Equal(Platform.Unknown, context.Platform);
        Assert.Equal(DeviceClass.Unknown, context.DeviceClass);
        Assert.Equal(ClientChannel.Unknown, context.Channel);
        Assert.False(context.IsCrawler);
    }

    [Fact]
    public void Classify_TheSameUserAgentTwice_ReturnsTheSameFacts()
    {
        ClientClassifier classifier = Classifier();

        const string userAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

        ClientContext first = classifier.Classify(Request(userAgent));
        ClientContext second = classifier.Classify(Request(userAgent));

        // The parse is memoised; the memo must not change the answer.
        Assert.Equal(first.Platform, second.Platform);
        Assert.Equal(first.DeviceClass, second.DeviceClass);
        Assert.Equal(first.Channel, second.Channel);
        Assert.Equal(first.OsVersion, second.OsVersion);
    }

    [Fact]
    public void Classify_MemoisedFacts_DoNotLeakOneRequestsContextIntoAnother()
    {
        ClientClassifier classifier = Classifier();

        const string userAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8 Pro) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/125.0.6422.72 Mobile Safari/537.36";

        ClientContext first = classifier.Classify(Request(userAgent, acceptLanguage: "sk-SK", referrer: "https://a.example/x"));
        ClientContext second = classifier.Classify(Request(userAgent, acceptLanguage: "de-DE", referrer: "https://b.example/y"));

        Assert.Equal("sk", first.Language);
        Assert.Equal("de", second.Language);
        Assert.Equal("a.example", first.ReferrerHost);
        Assert.Equal("b.example", second.ReferrerHost);
    }

    [Theory]
    [InlineData("sk-SK,sk;q=0.9,en-US;q=0.8,en;q=0.7", "sk")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("de;q=0.7", "de")]
    [InlineData("  fr  ", "fr")]
    [InlineData("zh-Hans-CN,zh;q=0.9", "zh")]
    [InlineData("CS", "cs")]
    public void Classify_AcceptLanguage_ReducesToThePrimarySubtag(string header, string expected) =>
        Assert.Equal(expected, Classifier().Classify(Request(acceptLanguage: header)).Language);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("en_US")]
    [InlineData("1234")]
    [InlineData("toolongsubtag")]
    [InlineData(";q=0.5")]
    [InlineData(",,,")]
    public void Classify_MalformedAcceptLanguage_YieldsNoLanguage(string? header) =>
        Assert.Null(Classifier().Classify(Request(acceptLanguage: header)).Language);

    [Theory]
    [InlineData("https://news.example.com/article?id=42", "news.example.com")]
    [InlineData("https://WWW.Example.COM/path", "example.com")]
    [InlineData("http://example.com", "example.com")]
    public void Classify_Referrer_IsReducedToItsHost(string referrer, string expected) =>
        Assert.Equal(expected, Classifier().Classify(Request(referrer: referrer)).ReferrerHost);

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_UnusableReferrer_YieldsNoHost(string? referrer) =>
        Assert.Null(Classifier().Classify(Request(referrer: referrer)).ReferrerHost);

    [Fact]
    public void Classify_CarriesTheRequestsOwnTimestampAndAddress()
    {
        IPAddress address = IPAddress.Parse("203.0.113.9");

        ClientContext context = Classifier().Classify(new ClientRequest
        {
            Host = "go.example",
            Path = "/abc",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
            RemoteIp = address,
            ReceivedAt = Now,
        });

        Assert.Equal(Now, context.ReceivedAt);
        Assert.Same(address, context.RemoteIp);
    }

    [Fact]
    public void Classify_NullRequest_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Classifier().Classify(null!));

    private static ClientRequest Request(
        string? userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
        string? acceptLanguage = null,
        string? referrer = null) => new()
        {
            Host = "go.example",
            Path = "/abc",
            UserAgent = userAgent,
            AcceptLanguage = acceptLanguage,
            Referrer = referrer,
            RemoteIp = IPAddress.Parse("203.0.113.9"),
            Query = ReadOnlyDictionary<string, string>.Empty,
            ReceivedAt = Now,
        };
}
