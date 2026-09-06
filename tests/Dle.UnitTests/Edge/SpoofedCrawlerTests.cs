using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;

using Dle.Domain.Analytics;
using Dle.Domain.Clients;
using Dle.Domain.Links;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.Edge.Clients;
using Dle.Edge.Configuration;
using Dle.Edge.RateLimiting;
using Dle.Edge.Resolution;
using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Configuration;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// The resolve pipeline of §B.6.1 driven directly, with fakes in place of the store, the crawler
/// confirmation and the click sink. There is no server and no network here: the point is the two
/// decisions that cannot be observed from the outside — what a forged crawler claim is turned into
/// (TC-107), and whether two different reasons for a 404 are distinguishable (TC-102, TC-166).
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2012:Use ValueTasks correctly",
    Justification = "NSubstitute configures a ValueTask-returning member by writing the call itself as the " +
                    "receiver of Returns. No task is awaited there; the expression is a recording of the " +
                    "call, and this is the only shape the library offers.")]
public sealed class SpoofedCrawlerTests : IAsyncLifetime
{
    private const string GooglebotUserAgent =
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>
    /// One classifier for the whole class: building one compiles the user agent regular expression
    /// cascade, which costs about a second, and the type is immutable and safe to share.
    /// </summary>
    private static readonly ClientClassifier SharedClassifier =
        new(NullGeoIpResolver.Instance, Options.Create(new EdgeOptions()));

    private readonly ServiceProvider _services;
    private readonly EdgeMetrics _metrics = new(meterFactory: null, new DomainVerificationFailureRegistry());
    private readonly RecordingClickSink _sink = new();
    private readonly ILinkStore _links = Substitute.For<ILinkStore>();
    private readonly IDomainConfigStore _domains = Substitute.For<IDomainConfigStore>();
    private readonly IBotVerifier _botVerifier = Substitute.For<IBotVerifier>();
    private readonly NotFoundEnumerationGuard _guard;
    private readonly LinkResolver _resolver;

    public SpoofedCrawlerTests()
    {
        var services = new ServiceCollection();

        // Results.Redirect and Results.Problem resolve a logger factory out of the request services,
        // so a bare container is not enough to execute what the pipeline returns.
        _ = services.AddLogging();
        _ = services.AddHybridCache();

        _services = services.BuildServiceProvider();

        var clock = new EdgeTestClock(Now);

        _guard = new NotFoundEnumerationGuard(
            Options.Create(new EdgeRateLimitOptions()),
            clock,
            _metrics,
            NullLogger<NotFoundEnumerationGuard>.Instance);

        _resolver = new LinkResolver(
            _services.GetRequiredService<HybridCache>(),
            _links,
            _domains,
            SharedClassifier,
            _botVerifier,
            new RoutingEngine(clock),
            _sink,
            new StubClickIdCodec(),
            new Dle.Crypto.IpHasher("unit-test-ip-hash-secret-0123456789"u8.ToArray(), TimeSpan.FromHours(24)),
            _guard,
            _metrics,
            new EdgeCacheOptions(),
            Options.Create(new EdgeOptions()),
            Options.Create(new EdgePrivacyOptions { IpStorage = IpStorageModes.Full }),
            clock,
            NullLogger<LinkResolver>.Instance);

        _domains.GetDomainAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<DomainRuntimeConfig?>(null));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _guard.Dispose();
        _metrics.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>
    /// TC-107. A user agent claiming to be Googlebot whose reverse DNS does not confirm the claim is
    /// served as the ordinary browser it actually is — a redirect, not an Open Graph document — and
    /// the click is recorded with <c>is_bot=false</c> and <c>spoofed_bot=true</c>. Both halves
    /// matter: a forged user agent must not be a free switch for keeping clicks out of, or pushing
    /// them into, somebody's campaign numbers.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-107")]
    public async Task Resolve_UnconfirmedGooglebotClaim_IsServedAsAnOrdinaryClientAndRecordedAsSpoofed()
    {
        GivenLink("go.example", "abc123", TenantA);

        _botVerifier.IsGenuineAsync(Arg.Any<string>(), Arg.Any<IPAddress?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));

        RenderedPage page = await ResolveAsync("go.example", "abc123", GooglebotUserAgent);

        Assert.Equal(StatusCodes.Status302Found, page.Status);
        Assert.StartsWith("https://shop.example/summer", page.Headers.Location.ToString(), StringComparison.Ordinal);

        ClickEvent recorded = Assert.Single(_sink.Events);

        Assert.False(recorded.IsBot);
        Assert.True(recorded.SpoofedBot);
        Assert.Equal(ChannelNames.Browser, recorded.Channel);
    }

    /// <summary>
    /// The other half of TC-107: a claim the verifier does confirm keeps its crawler treatment — an
    /// Open Graph document (TC-106) and <c>is_bot=true</c>.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-107")]
    public async Task Resolve_ConfirmedGooglebot_KeepsItsCrawlerTreatment()
    {
        GivenLink("go.example", "abc123", TenantA);

        _botVerifier.IsGenuineAsync(Arg.Any<string>(), Arg.Any<IPAddress?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        RenderedPage page = await ResolveAsync("go.example", "abc123", GooglebotUserAgent);

        Assert.Equal(StatusCodes.Status200OK, page.Status);
        Assert.Contains("og:title", page.Html, StringComparison.Ordinal);

        ClickEvent recorded = Assert.Single(_sink.Events);

        Assert.True(recorded.IsBot);
        Assert.False(recorded.SpoofedBot);
        Assert.Equal(ChannelNames.Crawler, recorded.Channel);
    }

    /// <summary>
    /// TC-102 and TC-166. A slug that never existed and a slug whose link lives on another tenant's
    /// host must produce the same answer — the same status and the same bytes — because anything
    /// else is an oracle that says which slugs exist.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-166")]
    public async Task Resolve_MissingSlugAndAnotherTenantsSlug_AreTheSameAnswer()
    {
        // "shared" exists, but on tenant B's host. On tenant A's host it is as absent as "nothing".
        GivenLink("b.example", "shared", TenantB);

        _links.FindAsync("a.example", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<LinkSnapshot?>(null));

        RenderedPage missing = await ResolveAsync("a.example", "nothing", BrowserUserAgent);
        RenderedPage foreign = await ResolveAsync("a.example", "shared", BrowserUserAgent);

        Assert.Equal(StatusCodes.Status404NotFound, missing.Status);
        Assert.Equal(StatusCodes.Status404NotFound, foreign.Status);

        // The documents are identical apart from the per-response nonce, which is a fixed length and
        // is not derived from anything about the request, so neither the body nor its length says
        // which of the two cases occurred.
        Assert.Equal(missing.HtmlWithoutNonces, foreign.HtmlWithoutNonces);
        Assert.Equal(missing.Body.Length, foreign.Body.Length);
        Assert.Equal(missing.Headers.ContentLength, foreign.Headers.ContentLength);
        Assert.Equal(missing.Headers.CacheControl.ToString(), foreign.Headers.CacheControl.ToString());
    }

    [Fact]
    [Trait("TestCase", "TC-103")]
    public async Task Resolve_QuarantinedLink_Returns410AndNeverRedirects()
    {
        GivenLink("go.example", "abc123", TenantA, link => link with { QuarantinedAt = Now.AddDays(-1) });

        RenderedPage page = await ResolveAsync("go.example", "abc123", BrowserUserAgent);

        Assert.Equal(StatusCodes.Status410Gone, page.Status);
        Assert.Empty(page.Headers.Location.ToString());
    }

    [Fact]
    [Trait("TestCase", "TC-104")]
    public async Task Resolve_ExpiredLinkWithALandingPage_RedirectsThere()
    {
        GivenLink(
            "go.example",
            "abc123",
            TenantA,
            link => link with { ExpiresAt = Now.AddDays(-1), ExpiredUrl = "https://shop.example/sale-over" });

        RenderedPage page = await ResolveAsync("go.example", "abc123", BrowserUserAgent);

        Assert.Equal(StatusCodes.Status302Found, page.Status);
        Assert.Equal("https://shop.example/sale-over", page.Headers.Location.ToString());
    }

    [Fact]
    [Trait("TestCase", "TC-104")]
    public async Task Resolve_ExpiredLinkWithoutALandingPage_IsIndistinguishableFromAMiss()
    {
        GivenLink("go.example", "abc123", TenantA, link => link with { ExpiresAt = Now.AddDays(-1) });

        _links.FindAsync("go.example", "nothing", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<LinkSnapshot?>(null));

        RenderedPage expired = await ResolveAsync("go.example", "abc123", BrowserUserAgent);
        RenderedPage missing = await ResolveAsync("go.example", "nothing", BrowserUserAgent);

        Assert.Equal(StatusCodes.Status404NotFound, expired.Status);
        Assert.Equal(missing.HtmlWithoutNonces, expired.HtmlWithoutNonces);
        Assert.Equal(missing.Body.Length, expired.Body.Length);
    }

    [Fact]
    public async Task Resolve_PreviewRequest_RecordsNoClick()
    {
        GivenLink("go.example", "abc123", TenantA);

        RenderedPage page = await ResolveAsync("go.example", "abc123", BrowserUserAgent, preview: true);

        // FR-166: the whole pipeline runs, and the campaign's numbers do not move.
        Assert.Equal(StatusCodes.Status302Found, page.Status);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task Resolve_WhenTheStoreIsUnavailable_Returns503AndNever500()
    {
        _links.FindAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<LinkSnapshot?>>(_ => throw new InvalidOperationException("the database is down"));

        RenderedPage page = await ResolveAsync("go.example", "abc123", BrowserUserAgent);

        // §D.6: an unavailable dependency is 503, because that is a difference every load balancer
        // and every client retry policy acts on.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, page.Status);
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private void GivenLink(
        string host,
        string slug,
        Guid tenantId,
        Func<LinkSnapshot, LinkSnapshot>? adjust = null)
    {
        LinkSnapshot link = new()
        {
            Id = 1,
            TenantId = tenantId,
            DomainId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Slug = slug,
            TargetUrl = "https://shop.example/summer",
            RoutingRules =
            [
                new RoutingRule
                {
                    Id = "default",
                    Then = new RuleAction
                    {
                        Action = RoutingActionKind.Web,
                        Url = "https://shop.example/summer",
                        Interstitial = InterstitialMode.Never,
                    },
                },
            ],
            Og = new OgMeta { Title = "Summer sale", Description = "Everything must go." },
            Utm = ReadOnlyDictionary<string, string>.Empty,
            IsActive = true,
            TenantConsentMode = ConsentMode.Full,
        };

        LinkSnapshot stored = adjust is null ? link : adjust(link);

        _links.FindAsync(host, slug, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<LinkSnapshot?>(stored));
    }

    private async Task<RenderedPage> ResolveAsync(string host, string slug, string userAgent, bool preview = false)
    {
        var context = new DefaultHttpContext { RequestServices = _services };

        context.Request.Method = HttpMethods.Get;
        context.Request.Host = new HostString(host);
        context.Request.Path = "/" + slug;
        context.Request.Headers.UserAgent = userAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        if (preview)
        {
            context.Request.QueryString = new QueryString("?_dl=preview");
        }

        using var body = new MemoryStream();
        context.Response.Body = body;

        IResult result = await _resolver.ResolveAsync(context, slug, TestContext.Current.CancellationToken);

        await result.ExecuteAsync(context);

        return new RenderedPage(context.Response.StatusCode, context.Response.Headers, body.ToArray());
    }

    /// <summary>Captures what the pipeline decided to record, which is half of TC-107.</summary>
    private sealed class RecordingClickSink : IClickEventSink
    {
        private readonly List<ClickEvent> _events = [];

        internal IReadOnlyList<ClickEvent> Events => _events;

        public long DroppedCount => 0;

        public bool TryWrite(ClickEvent clickEvent)
        {
            _events.Add(clickEvent);
            return true;
        }
    }

    /// <summary>
    /// A click identifier codec with no key material: the resolver only needs an opaque string, and
    /// the codec itself is covered exhaustively by <c>ClickIdCodecTests</c>.
    /// </summary>
    private sealed class StubClickIdCodec : Dle.Domain.Crypto.IClickIdCodec
    {
        private int _counter;

        public string New(DateTimeOffset occurredAt) =>
            "clk" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool TryDecode(string clickId, out DateTimeOffset occurredAt, out long sequence)
        {
            occurredAt = default;
            sequence = 0;
            return false;
        }
    }
}
