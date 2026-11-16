using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

using Dle.Crypto;
using Dle.Domain.Analytics;
using Dle.Domain.Attribution;
using Dle.Domain.Clients;
using Dle.Domain.Crypto;
using Dle.Domain.Entities;
using Dle.Domain.Links;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.Edge.Clients;
using Dle.Edge.Configuration;
using Dle.Edge.RateLimiting;
using Dle.Edge.Resolution;
using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Attribution;
using Dle.Persistence.Fast.Configuration;
using Dle.UnitTests.Crypto;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using NSubstitute;

using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// Verifies Step 5a (<c>click_events.extra["deeplink_path"]</c>) and Step 5b (the zero-JS deferred
/// attribution consent form on full-consent interstitials backed by <c>POST /_dl/consent</c>).
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2012:Use ValueTasks correctly",
    Justification = "NSubstitute records ValueTask returns in test setup.")]
public sealed partial class DeferredConsentTests : IAsyncLifetime
{
    private const string IosInstagramUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/21F90 Instagram 330.0.0.0.1";

    private const string AndroidInstagramUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36 Instagram 330.0.0.0.1";

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly ClientClassifier SharedClassifier =
        new(NullGeoIpResolver.Instance, Options.Create(new EdgeOptions()));

    private readonly ServiceProvider _services;
    private readonly EdgeMetrics _metrics = new(meterFactory: null, new DomainVerificationFailureRegistry());
    private readonly RecordingClickSink _sink = new();
    private readonly RecordingClaimCodeWriter _claimWriter = new();
    private readonly ILinkStore _links = Substitute.For<ILinkStore>();
    private readonly IDomainConfigStore _domains = Substitute.For<IDomainConfigStore>();
    private readonly IBotVerifier _botVerifier = Substitute.For<IBotVerifier>();
    private readonly NotFoundEnumerationGuard _guard;
    private readonly SignedTokenCodec _tokenCodec;
    private readonly LinkResolver _resolver;

    public DeferredConsentTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        _services = services.BuildServiceProvider();

        var clock = new EdgeTestClock(Now);

        _guard = new NotFoundEnumerationGuard(
            Options.Create(new EdgeRateLimitOptions()),
            clock,
            _metrics,
            NullLogger<NotFoundEnumerationGuard>.Instance);

        var cryptoOptions = new CryptoOptions
        {
            MasterSecret = CryptoTestKeys.MasterSecret,
            SigningAlgorithm = SignatureAlgorithms.Ed25519,
        };

        SigningKeyMaterial key = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519,
            SigningKeyPurposes.Token,
            notBefore: Now.AddDays(-1),
            notAfter: Now.AddDays(30));

        _tokenCodec = new SignedTokenCodec(
            new StaticKeyRing(key, clock),
            new InMemoryTokenReplayGuard(cryptoOptions.ReplayGuardCapacity, clock, NullLogger<InMemoryTokenReplayGuard>.Instance),
            Options.Create(cryptoOptions),
            clock);

        _resolver = new LinkResolver(
            _services.GetRequiredService<HybridCache>(),
            _links,
            _domains,
            SharedClassifier,
            _botVerifier,
            new RoutingEngine(clock),
            _sink,
            new StubClickIdCodec(),
            new IpHasher("unit-test-ip-hash-secret-0123456789"u8.ToArray(), TimeSpan.FromHours(24)),
            _guard,
            _metrics,
            new EdgeCacheOptions(),
            Options.Create(new EdgeOptions()),
            Options.Create(new EdgePrivacyOptions { IpStorage = IpStorageModes.Full }),
            clock,
            NullLogger<LinkResolver>.Instance,
            new ClaimCodeGenerator("unit-test-claim-secret-0123456789abcdef"u8),
            _claimWriter,
            _tokenCodec);

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

    [Theory]
    [InlineData(ConsentMode.Full, "link-full", true)]
    [InlineData(ConsentMode.AggregateOnly, "link-aggregateonly", true)]
    [InlineData(ConsentMode.Off, "link-off", false)]
    public async Task Resolve_RecordsDeeplinkPathInClickEventExtra_UnlessConsentOff(
        ConsentMode mode,
        string slug,
        bool expectedStored)
    {
        GivenStoreInterstitialLink("go.example", slug, mode, "/product/42");

        _ = await ResolveAsync("go.example", slug, AndroidInstagramUserAgent);

        ClickEvent recorded = Assert.Single(_sink.Events);
        if (expectedStored)
        {
            Assert.NotNull(recorded.Extra);
            Assert.Equal("/product/42", recorded.Extra[DapperClickLookup.DeeplinkPathKey]);
        }
        else
        {
            Assert.Null(recorded.Extra);
        }
    }

    [Fact]
    public async Task Resolve_And_PostConsent_AndroidInterstitial_UnlocksPlayStoreClickIdAndConsumesToken()
    {
        GivenStoreInterstitialLink("go.example", "android-offer", ConsentMode.Full, "/offer/android");

        // 1. Initial GET without consent emits zero-JS opt-in form and strips dl_cid from store URL.
        RenderedPage initial = await ResolveAsync("go.example", "android-offer", AndroidInstagramUserAgent);

        Assert.Equal(StatusCodes.Status200OK, initial.Status);
        Assert.Contains("form-action 'self';", initial.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("action=\"/_dl/consent\"", initial.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("dl_cid", initial.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-dle-delay", initial.Html, StringComparison.Ordinal);

        string token = ExtractHiddenToken(initial.Html);

        // 2. Submitting the form unlocks dl_cid on Play Store URL, restores form-action 'none', and logs Full click.
        RenderedPage consented = await PostConsentAsync("go.example", token, AndroidInstagramUserAgent);

        Assert.Equal(StatusCodes.Status200OK, consented.Status);
        Assert.Contains("form-action 'none';", consented.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/_dl/consent\"", consented.Html, StringComparison.Ordinal);
        Assert.Contains("play.google.com", consented.Html, StringComparison.Ordinal);
        Assert.Contains("dl_cid", consented.Html, StringComparison.Ordinal);

        Assert.Equal(2, _sink.Events.Count);
        Assert.Null(_sink.Events[0].IpPrefix);
        Assert.Equal("full", _sink.Events[1].ConsentMode);
        Assert.Equal("203.0.113.0/24", _sink.Events[1].IpPrefix);
        Assert.NotEqual(_sink.Events[0].ClickId, _sink.Events[1].ClickId);
        Assert.Equal("/offer/android", _sink.Events[1].Extra![DapperClickLookup.DeeplinkPathKey]);

        // 3. Replaying the same single-use token is rejected with 404.
        RenderedPage replay = await PostConsentAsync("go.example", token, AndroidInstagramUserAgent);
        Assert.Equal(StatusCodes.Status404NotFound, replay.Status);
    }

    [Fact]
    public async Task Resolve_IosInterstitial_WithIssuedClaimCode_DoesNotShowRedundantConsentForm()
    {
        GivenStoreInterstitialLink("go.example", "ios-offer", ConsentMode.Full, "/offer/spring");

        RenderedPage page = await ResolveAsync("go.example", "ios-offer", IosInstagramUserAgent);

        Assert.Equal(StatusCodes.Status200OK, page.Status);
        Assert.Contains("dle-code", page.Html, StringComparison.Ordinal);
        Assert.Single(_claimWriter.Records);
        Assert.DoesNotContain("action=\"/_dl/consent\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("form-action 'none';", page.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostConsent_WrongHostOrGpcHeader_IsRejected()
    {
        GivenStoreInterstitialLink("go.example", "offer", ConsentMode.Full, "/offer/1");
        RenderedPage initial = await ResolveAsync("go.example", "offer", AndroidInstagramUserAgent);
        string token = ExtractHiddenToken(initial.Html);

        RenderedPage wrongHost = await PostConsentAsync("other.example", token, AndroidInstagramUserAgent);
        Assert.Equal(StatusCodes.Status404NotFound, wrongHost.Status);

        RenderedPage gpcRejected = await PostConsentAsync("go.example", token, AndroidInstagramUserAgent, gpc: true);
        Assert.Equal(StatusCodes.Status403Forbidden, gpcRejected.Status);
        Assert.Single(_sink.Events);
    }

    private void GivenStoreInterstitialLink(string host, string slug, ConsentMode mode, string deeplinkPath)
    {
        LinkSnapshot link = new()
        {
            Id = 42,
            TenantId = TenantA,
            DomainId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Slug = slug,
            TargetUrl = "https://shop.example/summer",
            DeeplinkPath = deeplinkPath,
            RoutingRules =
            [
                new RoutingRule
                {
                    Id = "store-interstitial",
                    Then = new RuleAction
                    {
                        Action = RoutingActionKind.AppOrStore,
                        DeeplinkPath = deeplinkPath,
                        Interstitial = InterstitialMode.Always,
                    },
                },
            ],
            Og = new OgMeta { Title = "Offer" },
            Utm = ReadOnlyDictionary<string, string>.Empty,
            IsActive = true,
            TenantConsentMode = mode,
            IosCustomScheme = "myapp",
            AndroidCustomScheme = "myapp",
            IosStoreUrl = "https://apps.apple.com/app/id123456",
            AndroidStoreUrl = "https://play.google.com/store/apps/details?id=com.example.app",
        };

        _links.FindAsync(host, slug, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<LinkSnapshot?>(link));
    }

    private async Task<RenderedPage> ResolveAsync(string host, string slug, string userAgent)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Host = new HostString(host);
        context.Request.Path = "/" + slug;
        context.Request.Headers.UserAgent = userAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        using var body = new MemoryStream();
        context.Response.Body = body;

        IResult result = await _resolver.ResolveAsync(context, slug, TestContext.Current.CancellationToken);
        await result.ExecuteAsync(context);

        return new RenderedPage(context.Response.StatusCode, context.Response.Headers, body.ToArray());
    }

    private async Task<RenderedPage> PostConsentAsync(string host, string token, string userAgent, bool gpc = false)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = HttpMethods.Post;
        context.Request.Host = new HostString(host);
        context.Request.Path = "/_dl/consent";
        context.Request.Headers.UserAgent = userAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["token"] = token,
        });

        if (gpc)
        {
            context.Request.Headers["Sec-GPC"] = "1";
        }

        using var body = new MemoryStream();
        context.Response.Body = body;

        IResult result = await _resolver.HandleConsentAsync(context, TestContext.Current.CancellationToken);
        await result.ExecuteAsync(context);

        return new RenderedPage(context.Response.StatusCode, context.Response.Headers, body.ToArray());
    }

    private static string ExtractHiddenToken(string html)
    {
        Match match = HiddenTokenRegex().Match(html);
        Assert.True(match.Success, "Expected hidden consent token input in HTML.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"token\" value=\"([^\"]+)\">", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenTokenRegex();

    private sealed class RecordingClickSink : IClickEventSink
    {
        internal List<ClickEvent> Events { get; } = [];
        public long DroppedCount => 0;

        public bool TryWrite(ClickEvent clickEvent)
        {
            Events.Add(clickEvent);
            return true;
        }
    }

    private sealed class RecordingClaimCodeWriter : IClaimCodeWriter
    {
        internal List<ClaimCodeRecord> Records { get; } = [];

        public ValueTask WriteAsync(ClaimCodeRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubClickIdCodec : IClickIdCodec
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

    private sealed class StaticKeyRing : IKeyRing
    {
        internal StaticKeyRing(SigningKeyMaterial material, TimeProvider timeProvider)
        {
            CurrentSigner = SigningKeyFactory.CreateSigner(material);
            Verifier = new MultiAlgorithmVerifier(
                [material.AlgorithmId],
                [SigningKeyFactory.CreateVerificationKey(material)],
                timeProvider);
        }

        public ISigner CurrentSigner { get; }
        public IVerifier Verifier { get; }
        public JwksDocument GetJwks() => throw new NotSupportedException();
        public ValueTask RotateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
