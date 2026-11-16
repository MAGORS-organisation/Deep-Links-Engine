using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Dle.Control.Features.Attribution;
using Dle.Control.Identity;
using Dle.Crypto;
using Dle.Domain.Attribution;
using Dle.Domain.Contracts;
using Dle.Domain.Crypto;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Persistence.Fast.Attribution;
using Dle.Persistence.Fast.Caching;
using Dle.Persistence.Fast.Configuration;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Covers A2: <c>POST /v1/login-hints</c>, <c>ILoginHintStore</c> <c>O(1)</c> indexed S2
/// lookup inside <see cref="ResolveInstall"/>, Valkey write-through cache hits in
/// <see cref="DapperLoginHintStore"/>, consent gating, and tenant isolation.
/// </summary>
public sealed class LoginHintTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid AppA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly SdkCaller CallerA = new(TenantA, AppA, Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private readonly FakeAttributionStore _store = new();
    private readonly FakeClickLookup _clicks = new();
    private readonly InMemoryLoginHintStore _hints = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly AttributionMetrics _metrics = new(meterFactory: null);
    private readonly ControlTestClock _clock = new(Now);
    private readonly ClickIdCodec _clickIds = new(
        "unit-test-click-id-permutation-key!"u8.ToArray(),
        "unit-test-click-id-mac-key-000000!!"u8.ToArray());

    private readonly ClaimCodeGenerator _claimCodeGenerator = new("unit-test-claim-code-secret-0123456"u8.ToArray());
    private readonly LoginKeyHasher _loginKeys = new(Options.Create(new CryptoOptions
    {
        MasterSecret = "unit-test-master-secret-0123456789abcdef",
    }));

    private readonly IpHasher _ipHasher = new("unit-test-ip-hash-secret-0123456789!!"u8.ToArray(), TimeSpan.FromDays(1));
    private readonly AttributionOptions _options = new();

    public void Dispose() => _metrics.Dispose();

    [Fact]
    public async Task TryLoginAsync_UsesIndexedHintStoreWithoutScanningClickStream()
    {
        _store.AddLink(new AttributedLink(
            Id: 42,
            TenantId: TenantA,
            DeeplinkPath: "/account/welcome",
            Campaign: "q3-login",
            Title: "Welcome",
            Parameters: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utm_source"] = "newsletter",
            })));

        string clickId = _clickIds.New(Now.AddMinutes(-5));
        byte[] loginHash = _loginKeys.Hash("user-alice@example.com");

        await _hints.RecordAsync(
            new ClickLoginHint
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                LoginKeyHash = loginHash,
                ClickId = clickId,
                LinkId = 42,
                DeeplinkPath = "/promo/vip",
                OccurredAt = Now.AddMinutes(-5),
                ExpiresAt = Now.AddHours(24),
            },
            CancellationToken.None);

        var scanFailClicks = new FailOnCandidateScanClickLookup();
        ResolveInstall engine = CreateEngine(scanFailClicks, _hints);

        ResolveOutcome outcome = await engine.ExecuteAsync(
            new ResolveRequestDto
            {
                InstallId = "inst-o1-login-0001",
                Platform = "ios",
                LoginKey = "user-alice@example.com",
                Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Now },
            },
            CallerA,
            IPAddress.Parse("203.0.113.10"),
            CancellationToken.None);

        Assert.Null(outcome.ClaimCodeError);
        Assert.NotNull(outcome.Response);
        Assert.True(outcome.Response.Matched);
        Assert.Equal("login", outcome.Response.MatchType);
        Assert.Equal(clickId, outcome.Response.ClickId);
        Assert.Equal("42", outcome.Response.Link?.Id);
        Assert.Equal("/promo/vip", outcome.Response.Link?.DeeplinkPath);
        Assert.Equal("newsletter", outcome.Response.Params["utm_source"]);
    }

    [Fact]
    public async Task TryLoginAsync_FallsBackToClickStreamCandidates_WhenNoHintExists()
    {
        _store.AddLink(new AttributedLink(
            Id: 77,
            TenantId: TenantA,
            DeeplinkPath: "/legacy/home",
            Campaign: "legacy",
            Title: "Legacy",
            Parameters: ReadOnlyDictionary<string, string>.Empty));

        string clickId = _clickIds.New(Now.AddMinutes(-10));
        byte[] loginHash = _loginKeys.Hash("legacy-user");
        _clicks.Add(new ClickRecord
        {
            Id = Guid.NewGuid(),
            OccurredAt = Now.AddMinutes(-10),
            TenantId = TenantA,
            LinkId = 77,
            ClickId = clickId,
            DeeplinkPath = "/legacy/home",
            Extra = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LoginKeyHasher.ClickExtraKey] = LoginKeyHasher.ToHex(loginHash),
            }),
        });

        ResolveInstall engine = CreateEngine(_clicks, _hints);

        ResolveOutcome outcome = await engine.ExecuteAsync(
            new ResolveRequestDto
            {
                InstallId = "inst-fallback-login-01",
                Platform = "android",
                LoginKey = "legacy-user",
                Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Now },
            },
            CallerA,
            IPAddress.Parse("203.0.113.11"),
            CancellationToken.None);

        Assert.NotNull(outcome.Response);
        Assert.True(outcome.Response.Matched);
        Assert.Equal("login", outcome.Response.MatchType);
        Assert.Equal(clickId, outcome.Response.ClickId);
    }

    [Fact]
    public async Task PostLoginHints_DeclinesStorage_WhenAttributionConsentAbsent()
    {
        string clickId = _clickIds.New(Now.AddMinutes(-1));
        using var limiters = new AttributionRateLimiters(Options.Create(new AttributionRateLimitOptions()));

        (int statusCode, LoginHintResponseDto? response) = await InvokeRecordLoginHintAsync(
            new LoginHintRequestDto
            {
                LoginKey = "alice@example.com",
                ClickId = clickId,
                Consent = new ConsentDto { Analytics = true, Attribution = false, Ts = Now },
            },
            CallerA,
            limiters);

        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.NotNull(response);
        Assert.False(response.Accepted);
        Assert.Empty(_hints.Recorded);
    }

    [Fact]
    public async Task PostLoginHints_RejectsCrossTenantClick()
    {
        string foreignClickId = _clickIds.New(Now.AddMinutes(-2));
        _clicks.Add(new ClickRecord
        {
            Id = Guid.NewGuid(),
            OccurredAt = Now.AddMinutes(-2),
            TenantId = TenantB,
            LinkId = 9001,
            ClickId = foreignClickId,
        });

        using var limiters = new AttributionRateLimiters(Options.Create(new AttributionRateLimitOptions()));

        (int statusCode, _) = await InvokeRecordLoginHintAsync(
            new LoginHintRequestDto
            {
                LoginKey = "attacker@example.com",
                ClickId = foreignClickId,
                Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Now },
            },
            CallerA,
            limiters);

        Assert.Equal(StatusCodes.Status404NotFound, statusCode);
        Assert.Empty(_hints.Recorded);
    }

    [Fact]
    public async Task PostLoginHints_PersistsByUrlAndReconcilesS2Login()
    {
        _store.AddLink(
            new AttributedLink(
                Id: 88,
                TenantId: TenantA,
                DeeplinkPath: "/cart/recover",
                Campaign: "winback",
                Title: "Cart",
                Parameters: ReadOnlyDictionary<string, string>.Empty),
            host: "go.acme.sk",
            slug: "cart20");

        using var limiters = new AttributionRateLimiters(Options.Create(new AttributionRateLimitOptions()));

        (int statusCode, LoginHintResponseDto? response) = await InvokeRecordLoginHintAsync(
            new LoginHintRequestDto
            {
                LoginKey = "buyer-99",
                Url = "https://go.acme.sk/cart20",
                Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Now },
            },
            CallerA,
            limiters);

        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.NotNull(response);
        Assert.True(response.Accepted);
        Assert.Single(_hints.Recorded);

        ResolveInstall engine = CreateEngine(new FailOnCandidateScanClickLookup(), _hints);
        ResolveOutcome outcome = await engine.ExecuteAsync(
            new ResolveRequestDto
            {
                InstallId = "inst-url-hint-01",
                Platform = "ios",
                LoginKey = "buyer-99",
                Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Now },
            },
            CallerA,
            IPAddress.Parse("203.0.113.12"),
            CancellationToken.None);

        Assert.NotNull(outcome.Response);
        Assert.True(outcome.Response.Matched);
        Assert.Equal("login", outcome.Response.MatchType);
        Assert.Equal("88", outcome.Response.Link?.Id);
        Assert.Equal("/cart/recover", outcome.Response.Link?.DeeplinkPath);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2012", Justification = "NSubstitute mock return setup.")]
    public async Task DapperLoginHintStore_ReturnsValkeyCachedHintWithoutTouchingDatabase()
    {
        byte[] hash = _loginKeys.Hash("cached-user");
        string expectedKey = $"dle:lhint:{TenantA:N}:{Convert.ToHexStringLower(hash)}";

        var cachedEntry = new LoginHintCacheEntry(
            Id: Guid.NewGuid(),
            ClickId: "ck_cached_1",
            LinkId: 123,
            DeeplinkPath: "/vip",
            OccurredAt: Now.AddMinutes(-2),
            ExpiresAt: Now.AddHours(1));

        string json = JsonSerializer.Serialize(cachedEntry, FastPersistenceJsonContext.Default.LoginHintCacheEntry);

        IDatabase redisDb = Substitute.For<IDatabase>();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));
        redisDb.StringGetAsync((RedisKey)expectedKey, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)json));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(redisDb);

        await using NpgsqlDataSource unusedDb = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=unused").Build();
        var store = new DapperLoginHintStore(unusedDb, new FastPersistenceOptions(), redis);

        ClickLoginHint? result = await store.FindLatestAsync(
            TenantA,
            hash,
            Now.AddHours(-24),
            Now,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(cachedEntry.ClickId, result.ClickId);
        Assert.Equal(123, result.LinkId);
        Assert.Equal("/vip", result.DeeplinkPath);
    }

    private ResolveInstall CreateEngine(IClickLookup clickLookup, ILoginHintStore? hintStore)
    {
        var opt = Options.Create(_options);
        var claimCodes = new ClaimCodeService(_store, _claimCodeGenerator, _clock, _metrics, opt);

        return new ResolveInstall(
            _store,
            clickLookup,
            _clickIds,
            claimCodes,
            _loginKeys,
            _ipHasher,
            _outbox,
            _metrics,
            _clock,
            opt,
            NullLogger<ResolveInstall>.Instance,
            hintStore);
    }

    private async Task<(int StatusCode, LoginHintResponseDto? Body)> InvokeRecordLoginHintAsync(
        LoginHintRequestDto dto,
        SdkCaller caller,
        AttributionRateLimiters limiters)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using ServiceProvider sp = services.BuildServiceProvider();

        string payload = JsonSerializer.Serialize(dto, AttributionJsonContext.Default.LoginHintRequestDto);
        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(DleClaimTypes.TenantId, caller.TenantId.ToString()),
                    new Claim(DleClaimTypes.AppId, caller.AppId.ToString()),
                    new Claim(DleClaimTypes.ActorId, caller.SdkKeyId.ToString()),
                ],
                authenticationType: "dle-key")),
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.50");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();

        IResult result = await AttributionEndpointExtensions.RecordLoginHintAsync(
            context,
            _store,
            _clickIds,
            _clicks,
            _loginKeys,
            _hints,
            limiters,
            _metrics,
            _clock,
            new StaticOptionsMonitor<AttributionOptions>(_options),
            new StaticOptionsMonitor<AttributionRateLimitOptions>(new AttributionRateLimitOptions()),
            CancellationToken.None);

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;

        if (context.Response.StatusCode == StatusCodes.Status202Accepted)
        {
            LoginHintResponseDto? parsed = await JsonSerializer.DeserializeAsync(
                context.Response.Body,
                AttributionJsonContext.Default.LoginHintResponseDto);
            return (context.Response.StatusCode, parsed);
        }

        return (context.Response.StatusCode, null);
    }

    private sealed class InMemoryLoginHintStore : ILoginHintStore
    {
        public List<ClickLoginHint> Recorded { get; } = [];

        public ValueTask RecordAsync(ClickLoginHint hint, CancellationToken ct)
        {
            Recorded.Add(hint);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ClickLoginHint?> FindLatestAsync(
            Guid tenantId,
            byte[] loginKeyHash,
            DateTimeOffset minOccurredAt,
            DateTimeOffset now,
            CancellationToken ct)
        {
            ClickLoginHint? match = Recorded
                .Where(h =>
                    h.TenantId == tenantId &&
                    h.LoginKeyHash.AsSpan().SequenceEqual(loginKeyHash) &&
                    h.OccurredAt >= minOccurredAt &&
                    h.ExpiresAt > now)
                .OrderByDescending(h => h.OccurredAt)
                .FirstOrDefault();

            return ValueTask.FromResult(match);
        }
    }

    private sealed class FailOnCandidateScanClickLookup : IClickLookup
    {
        public ValueTask<ClickRecord?> FindByClickIdAsync(string clickId, DateTimeOffset hintFrom, DateTimeOffset hintTo, CancellationToken ct) =>
            ValueTask.FromResult<ClickRecord?>(null);

        public ValueTask<IReadOnlyList<ClickRecord>> FindCandidatesAsync(
            Guid tenantId,
            DateTimeOffset from,
            DateTimeOffset to,
            string? ipPrefix,
            string? osFamily,
            CancellationToken ct) =>
            throw new InvalidOperationException("FindCandidatesAsync should not be called when an indexed S2 login hint exists.");
    }

    private sealed class RecordingOutbox : IWebhookOutbox
    {
        public Task EnqueueAsync(Guid tenantId, string eventType, string payloadJson, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
