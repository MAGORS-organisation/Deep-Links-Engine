using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Dle.Control.Features.Attribution;
using Dle.Crypto;
using Dle.Domain.Attribution;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// <c>POST /v1/resolve</c> — the deferred deep linking decision (§B.6.2, §B.6.3, FR-182 to FR-188).
/// Three properties are enforced by the use case rather than hoped for: consent is an input and not
/// a filter, one installation gets one attribution, and one click is worth one installation. Every
/// decision, including every refusal, has to leave behind an evidence document that explains it —
/// §B.5.3 exists because a disputed attribution has no other defence.
/// </summary>
public sealed class ResolveInstallTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid OtherTenant = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static readonly Guid App = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly SdkCaller Caller = new(Tenant, App, Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private readonly FakeAttributionStore _store = new();
    private readonly FakeClickLookup _clicks = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly AttributionMetrics _metrics = new(meterFactory: null);
    private readonly ControlTestClock _clock = new(Now);
    private readonly ClickIdCodec _clickIds = new(
        "unit-test-click-id-permutation-key!"u8.ToArray(),
        "unit-test-click-id-mac-key-000000!!"u8.ToArray());

    private readonly ClaimCodeGenerator _claimCodeGenerator = new("unit-test-claim-code-secret-0123456"u8.ToArray());

    private readonly AttributionOptions _options = new();

    public void Dispose() => _metrics.Dispose();

    /// <summary>
    /// TC-141. The Android install referrer carries <c>dl_cid</c>, the click exists and belongs to
    /// the caller: a deterministic match at confidence exactly 1.00, never an approximation
    /// (FR-186).
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-141")]
    public async Task Resolve_InstallReferrerCarryingAClickId_IsADeterministicMatch()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-12);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));
        _store.AddLink(new AttributedLink(
            77,
            Tenant,
            "/summer",
            "summer-2026",
            "Summer sale",
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utm_source"] = "google-play",
            })));

        ResolveOutcome outcome = await ResolveAsync(Request(referrer: Referrer(clickId)));

        ResolveResponseDto response = Assert.IsType<ResolveResponseDto>(outcome.Response);

        Assert.Null(outcome.ClaimCodeError);
        Assert.True(response.Matched);
        Assert.Equal(MatchTypeNames.InstallReferrer, response.MatchType);
        Assert.Equal(1.00m, response.Confidence);
        Assert.Equal(clickId, response.ClickId);
        Assert.Equal("77", response.Link?.Id);
        Assert.Equal("/summer", response.Link?.DeeplinkPath);
        Assert.Equal("summer-2026", response.Link?.Campaign);
        Assert.Equal("google-play", response.Params["utm_source"]);

        // §B.5.3: the row has to explain itself afterwards.
        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionStrategyNames.InstallReferrer, evidence[AttributionEvidence.StrategyKey]);
        Assert.Equal(ConsentGate.ReasonFullWithConsent, evidence[AttributionEvidence.ConsentKey]);
        Assert.Equal("12", evidence[AttributionEvidence.ElapsedMinutesKey]);
        Assert.Equal(
            clickedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            evidence[AttributionEvidence.ClickOccurredAtKey]);

        // The webhook that tells the tenant's systems about it (§B.7.4).
        Assert.Single(_outbox.Events);
        Assert.Equal(WebhookEvents.AttributionCreated, _outbox.Events[0].EventType);
    }

    [Fact]
    [Trait("TestCase", "TC-141")]
    public async Task Resolve_InstallReferrer_LooksTheClickUpInsideABoundedWindow()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-12);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));

        _ = await ResolveAsync(Request(referrer: Referrer(clickId)));

        // §B.6.3: the identifier carries its own timestamp so the query can prune partitions. A
        // lookup without a time bound is the debt that only becomes visible after months of data.
        Assert.Equal(1, _clicks.ClickIdLookups);
        Assert.NotNull(_clicks.LastClickIdWindow);

        (DateTimeOffset from, DateTimeOffset to) = _clicks.LastClickIdWindow!.Value;

        Assert.Equal(clickedAt.AddMinutes(-_options.ClickWindowMinutes), from);
        Assert.Equal(clickedAt.AddMinutes(_options.ClickWindowMinutes), to);
    }

    /// <summary>TC-142. An organic install is not an error; the application simply carries on.</summary>
    [Fact]
    [Trait("TestCase", "TC-142")]
    public async Task Resolve_ReferrerWithoutAClickId_IsNoneAndNotAnError()
    {
        ResolveOutcome outcome = await ResolveAsync(Request(referrer: "utm_source=google-play&utm_medium=organic"));

        ResolveResponseDto response = Assert.IsType<ResolveResponseDto>(outcome.Response);

        Assert.Null(outcome.ClaimCodeError);
        Assert.False(response.Matched);
        Assert.Equal(MatchTypeNames.None, response.MatchType);
        Assert.Equal(0m, response.Confidence);
        Assert.Null(response.ClickId);
        Assert.Null(response.Link);

        // A refusal is a decision, and it is recorded with the reason it was refused for.
        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.NoStrategyMatched, evidence[AttributionEvidence.ReasonKey]);
        Assert.Equal(AttributionReasons.NoClickId, evidence["refused." + AttributionStrategyNames.InstallReferrer]);
    }

    [Fact]
    [Trait("TestCase", "TC-142")]
    public async Task Resolve_ClickIdThatNoClickMatches_IsNoneWithTheReasonRecorded()
    {
        string clickId = _clickIds.New(Now.AddMinutes(-3));

        ResolveOutcome outcome = await ResolveAsync(Request(referrer: Referrer(clickId)));

        Assert.False(outcome.Response!.Matched);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.ClickNotFound, evidence["refused." + AttributionStrategyNames.InstallReferrer]);
    }

    /// <summary>
    /// TC-167 / T-05. A mutated click identifier fails its integrity check, is never matched, and is
    /// recorded as tampered rather than as an ordinary miss.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-167")]
    public async Task Resolve_TamperedClickId_IsRecordedAsTamperedAndNeverMatched()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-5);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));

        char[] mutated = clickId.ToCharArray();
        mutated[7] = mutated[7] == 'a' ? 'b' : 'a';

        ResolveOutcome outcome = await ResolveAsync(Request(referrer: Referrer(new string(mutated))));

        Assert.False(outcome.Response!.Matched);
        Assert.Equal(0, _clicks.ClickIdLookups);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal("true", evidence[AttributionEvidence.TamperedKey]);
        Assert.Equal(AttributionReasons.TamperedClickId, evidence["refused." + AttributionStrategyNames.InstallReferrer]);
    }

    [Fact]
    [Trait("TestCase", "TC-166")]
    public async Task Resolve_ClickBelongingToAnotherTenant_IsNoneRatherThanARefusal()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-4);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt) with { TenantId = OtherTenant });

        ResolveOutcome outcome = await ResolveAsync(Request(referrer: Referrer(clickId)));

        // A 403 would confirm that the identifier exists (SHARED-KERNEL §17.7).
        Assert.False(outcome.Response!.Matched);
        Assert.Null(outcome.ClaimCodeError);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.ForeignTenant, evidence["refused." + AttributionStrategyNames.InstallReferrer]);
    }

    /// <summary>TC-143. The second call for one installation re-reads the decision; it never writes a second one.</summary>
    [Fact]
    [Trait("TestCase", "TC-143")]
    public async Task Resolve_SameInstallTwice_IsIdempotentAndWritesOneAttribution()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-8);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));
        _store.AddLink(new AttributedLink(77, Tenant, "/summer", "summer-2026", "Summer sale",
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))));

        ResolveRequestDto request = Request(referrer: Referrer(clickId));

        ResolveOutcome first = await ResolveAsync(request);

        _clock.Now = Now.AddMinutes(30);

        ResolveOutcome second = await ResolveAsync(request);

        Assert.Single(_store.Attributions);
        Assert.Single(_store.Installs);
        Assert.Single(_outbox.Events);

        // The answer is the stored decision, byte for byte — including the parameters, which come
        // from the link and never from what the caller presented.
        Assert.Equal(first.Response!.MatchType, second.Response!.MatchType);
        Assert.Equal(first.Response.Confidence, second.Response.Confidence);
        Assert.Equal(first.Response.ClickId, second.Response.ClickId);
        Assert.Equal(first.Response.Link?.Id, second.Response.Link?.Id);
    }

    /// <summary>TC-144. One click is worth one installation, and the first one wins.</summary>
    [Fact]
    [Trait("TestCase", "TC-144")]
    public async Task Resolve_TwoInstallsPresentingOneClickId_OnlyTheFirstIsAttributed()
    {
        DateTimeOffset clickedAt = Now.AddMinutes(-6);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));

        ResolveOutcome first = await ResolveAsync(Request(installId: "install-A", referrer: Referrer(clickId)));
        ResolveOutcome second = await ResolveAsync(Request(installId: "install-B", referrer: Referrer(clickId)));

        Assert.True(first.Response!.Matched);
        Assert.Equal(MatchTypeNames.InstallReferrer, first.Response.MatchType);

        Assert.False(second.Response!.Matched);
        Assert.Equal(MatchTypeNames.None, second.Response.MatchType);
        Assert.Null(second.Response.ClickId);

        Assert.Equal(2, _store.Attributions.Count);

        AttributionRecord loser = _store.Attributions[1];

        // The refusal names the identifier it wanted, in the evidence document rather than in the
        // indexed column — writing it there is exactly what the unique index just refused.
        Assert.Null(loser.ClickId);
        Assert.Equal(MatchTypeNames.None, loser.MatchType);

        Dictionary<string, string> evidence = EvidenceOf(loser);

        Assert.Equal(AttributionReasons.ClickAlreadyClaimed, evidence[AttributionEvidence.ReasonKey]);
        Assert.Equal(clickId, evidence["refused_click_id"]);

        // Only the winner is announced.
        Assert.Single(_outbox.Events);
    }

    /// <summary>
    /// TC-145. The probabilistic module is off, and the caller sent signals anyway. They are
    /// discarded with the request: nothing reads them, so nothing can store them.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-145")]
    public async Task Resolve_ProbabilisticModuleDisabled_DiscardsTheSignalsAndAnswersNone()
    {
        _options.Probabilistic.Enabled = false;
        _options.Strategies.Add(AttributionStrategyNames.InstallReferrer);
        _options.Strategies.Add(AttributionStrategyNames.Probabilistic);

        _clicks.Add(Click("some-other-click", Now.AddMinutes(-2)) with
        {
            IpPrefix = "203.0.113.0/24",
            Language = "en",
            OsVersion = "17.5",
        });

        ResolveOutcome outcome = await ResolveAsync(Request(signals: Signals()));

        Assert.False(outcome.Response!.Matched);
        Assert.Equal(MatchTypeNames.None, outcome.Response.MatchType);

        // The strongest available assertion that the signals were never processed: no candidate
        // query happened at all.
        Assert.Equal(0, _clicks.CandidateQueries);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(
            AttributionReasons.ProbabilisticUnavailable,
            evidence["refused." + AttributionStrategyNames.Probabilistic]);

        Assert.DoesNotContain(evidence.Keys, key => key.StartsWith(AttributionEvidence.SignalKeyPrefix, StringComparison.Ordinal));
    }

    /// <summary>TC-146. The module is on, but the visitor refused attribution consent. Same answer.</summary>
    [Fact]
    [Trait("TestCase", "TC-146")]
    public async Task Resolve_WithoutAttributionConsent_ProcessesNoSignalsAndLooksUpNoClick()
    {
        _options.Probabilistic.Enabled = true;
        _options.Strategies.Add(AttributionStrategyNames.InstallReferrer);
        _options.Strategies.Add(AttributionStrategyNames.Probabilistic);

        DateTimeOffset clickedAt = Now.AddMinutes(-2);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt) with { IpPrefix = "203.0.113.0/24", Language = "en", OsVersion = "17.5" });

        ResolveOutcome outcome = await ResolveAsync(Request(
            referrer: Referrer(clickId),
            signals: Signals(),
            consent: new ConsentDto { Analytics = true, Attribution = false, Ts = Now }));

        Assert.False(outcome.Response!.Matched);
        Assert.Equal(MatchTypeNames.None, outcome.Response.MatchType);

        // The answer is not final: the host application may record consent later and the install
        // referrer stays valid for the referrer window, so the SDK is told when to ask again.
        Assert.True(
            outcome.Response.ExpiresIn > 0,
            "a no-match given for want of consent must carry a non-zero expires_in");

        // Without consent no click is looked up at all — consent is an input to the decision, not a
        // filter applied to a finished answer (§E.6.2).
        Assert.Equal(0, _clicks.CandidateQueries);
        Assert.Equal(0, _clicks.ClickIdLookups);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.ConsentMissing, evidence[AttributionEvidence.ReasonKey]);
        Assert.Equal(ConsentGate.ReasonNoAttributionConsent, evidence[AttributionEvidence.ConsentKey]);

        // The referrer is evidence of a linkage there is no basis to keep, so it is not stored.
        Install install = Assert.Single(_store.Installs);
        Assert.Null(install.RawReferrer);
    }

    [Fact]
    [Trait("TestCase", "TC-146")]
    public async Task Resolve_WithoutAConsentSignalAtAll_BehavesAsIfAttributionWasRefused()
    {
        _options.Probabilistic.Enabled = true;
        _options.Strategies.Add(AttributionStrategyNames.Probabilistic);

        ResolveOutcome outcome = await ResolveAsync(Request(signals: Signals(), withoutConsentSignal: true));

        Assert.False(outcome.Response!.Matched);
        Assert.Equal(0, _clicks.CandidateQueries);
    }

    /// <summary>
    /// TC-147. A candidate outside the window scores exactly zero, so the answer is <c>none</c> and
    /// never a weak match. The window is enforced twice — the query asks for it and the scorer
    /// insists on it — and this test defeats the first to prove the second.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-147")]
    public async Task Resolve_ProbabilisticCandidateOutsideTheWindow_IsNoneRatherThanAWeakMatch()
    {
        _options.Probabilistic.Enabled = true;
        _options.Probabilistic.WindowMinutes = 60;
        _options.Strategies.Add(AttributionStrategyNames.Probabilistic);

        // Ninety minutes ago, against a sixty minute window — and otherwise a perfect signal match.
        _clicks.Add(Click("stale-click", Now.AddMinutes(-90)) with
        {
            IpPrefix = "203.0.113.0/24",
            Language = "en",
            OsVersion = "17.5",
            OsFamily = "iOS",
        });

        ResolveOutcome outcome = await ResolveAsync(Request(
            platform: "ios",
            osVersion: "17.5",
            signals: Signals(),
            consent: Consented()));

        Assert.False(outcome.Response!.Matched);
        Assert.Equal(MatchTypeNames.None, outcome.Response.MatchType);
        Assert.Equal(0m, outcome.Response.Confidence);

        Assert.Equal(1, _clicks.CandidateQueries);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.OutsideWindow, evidence["refused." + AttributionStrategyNames.Probabilistic]);
    }

    [Fact]
    [Trait("TestCase", "TC-147")]
    public async Task Resolve_ProbabilisticCandidateInsideTheWindow_MatchesAndRecordsWhichSignalsAgreed()
    {
        _options.Probabilistic.Enabled = true;
        _options.Probabilistic.WindowMinutes = 60;
        _options.Strategies.Add(AttributionStrategyNames.Probabilistic);

        _clicks.Add(Click("fresh-click", Now.AddMinutes(-10)) with
        {
            LinkId = 77,
            IpPrefix = "203.0.113.0/24",
            Language = "en",
            OsVersion = "17.5",
            OsFamily = "iOS",
        });

        _store.AddLink(new AttributedLink(77, Tenant, "/summer", "summer-2026", "Summer sale",
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))));

        ResolveOutcome outcome = await ResolveAsync(Request(
            platform: "ios",
            osVersion: "17.5",
            signals: Signals(),
            consent: Consented()));

        Assert.True(outcome.Response!.Matched);
        Assert.Equal(MatchTypeNames.Probabilistic, outcome.Response.MatchType);

        // A probabilistic decision is never 1.00: that number is reserved for deterministic evidence.
        Assert.True(outcome.Response.Confidence < 1.00m);
        Assert.True(outcome.Response.Confidence >= _options.Probabilistic.MinConfidence);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionStrategyNames.Probabilistic, evidence[AttributionEvidence.StrategyKey]);
        Assert.Equal("3600", evidence[AttributionEvidence.WindowSecondsKey]);
        Assert.Equal("1", evidence[AttributionEvidence.CandidatesKey]);
        Assert.Contains(evidence.Keys, key => key.StartsWith(AttributionEvidence.SignalKeyPrefix, StringComparison.Ordinal));
        Assert.Equal("0.45", evidence[AttributionEvidence.SignalKeyPrefix + "ip_prefix"]);
    }

    /// <summary>
    /// TC-148. A claim code the user typed after it expired earns a typed error that says so, not a
    /// silent fall-through to a weaker strategy. The user asked a deterministic question.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-148")]
    public async Task Resolve_ExpiredClaimCode_IsATypedErrorRatherThanNone()
    {
        _options.Strategies.Add(AttributionStrategyNames.ClaimCode);

        ClaimCodeCredential credential = _claimCodeGenerator.Create();

        _store.AddClaimCode(new ClaimCodeRecord
        {
            TenantId = Tenant,
            CodeHash = credential.Hash,
            ClickId = "some-click",
            LinkId = 77,
            CreatedAt = Now.AddHours(-2),
            ExpiresAt = Now.AddHours(-1),
        });

        ResolveOutcome outcome = await ResolveAsync(Request(claimCode: credential.Code));

        Assert.Null(outcome.Response);
        Assert.Equal(ClaimCodeStatus.Expired, outcome.ClaimCodeError);

        // Nothing was written and nothing was spent: asking for a new code is a real remedy.
        Assert.Empty(_store.Attributions);
        Assert.Empty(_store.ConsumedClaimCodes);
    }

    [Fact]
    [Trait("TestCase", "TC-148")]
    public async Task Resolve_ValidClaimCode_MatchesAndSpendsTheCodeExactlyOnce()
    {
        _options.Strategies.Add(AttributionStrategyNames.ClaimCode);

        ClaimCodeCredential credential = _claimCodeGenerator.Create();

        _store.AddClaimCode(new ClaimCodeRecord
        {
            TenantId = Tenant,
            CodeHash = credential.Hash,
            ClickId = "claimed-click",
            LinkId = 77,
            CreatedAt = Now.AddMinutes(-10),
            ExpiresAt = Now.AddMinutes(50),
        });

        _store.AddLink(new AttributedLink(77, Tenant, "/summer", "summer-2026", "Summer sale",
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))));

        ResolveOutcome outcome = await ResolveAsync(Request(claimCode: credential.Code));

        Assert.Null(outcome.ClaimCodeError);
        Assert.True(outcome.Response!.Matched);
        Assert.Equal(MatchTypeNames.ClaimCode, outcome.Response.MatchType);
        Assert.Equal(1.00m, outcome.Response.Confidence);
        Assert.Single(_store.ConsumedClaimCodes);
    }

    [Theory]
    [Trait("TestCase", "TC-148")]
    [InlineData("ACDEFG", ClaimCodeStatus.Unknown)]
    [InlineData("!!!!!!", ClaimCodeStatus.Malformed)]
    [InlineData("TOOLONGCODE", ClaimCodeStatus.Malformed)]
    public async Task Resolve_UnusableClaimCode_IsATypedError(string code, ClaimCodeStatus expected)
    {
        _options.Strategies.Add(AttributionStrategyNames.ClaimCode);

        ResolveOutcome outcome = await ResolveAsync(Request(claimCode: code));

        Assert.Null(outcome.Response);
        Assert.Equal(expected, outcome.ClaimCodeError);
    }

    [Fact]
    [Trait("TestCase", "TC-148")]
    public async Task Resolve_ClaimCodeOfAnotherTenant_IsNoneRatherThanAnError()
    {
        _options.Strategies.Add(AttributionStrategyNames.ClaimCode);

        ClaimCodeCredential credential = _claimCodeGenerator.Create();

        _store.AddClaimCode(new ClaimCodeRecord
        {
            TenantId = OtherTenant,
            CodeHash = credential.Hash,
            ClickId = "claimed-click",
            CreatedAt = Now.AddMinutes(-10),
            ExpiresAt = Now.AddMinutes(50),
        });

        ResolveOutcome outcome = await ResolveAsync(Request(claimCode: credential.Code));

        // Existence of somebody else's code is not confirmed, so it degrades to "no match".
        Assert.Null(outcome.ClaimCodeError);
        Assert.False(outcome.Response!.Matched);

        Dictionary<string, string> evidence = EvidenceOf(Assert.Single(_store.Attributions));

        Assert.Equal(AttributionReasons.ForeignTenant, evidence["refused." + AttributionStrategyNames.ClaimCode]);
    }

    [Fact]
    public async Task Resolve_StrategiesRunInTheConfiguredOrderAndTheFirstMatchWins()
    {
        _options.Strategies.Add(AttributionStrategyNames.InstallReferrer);
        _options.Strategies.Add(AttributionStrategyNames.Login);

        DateTimeOffset clickedAt = Now.AddMinutes(-3);
        string clickId = _clickIds.New(clickedAt);

        _clicks.Add(Click(clickId, clickedAt));

        ResolveOutcome outcome = await ResolveAsync(Request(referrer: Referrer(clickId), loginKey: "user-42"));

        // The install referrer is first in the order and it matched, so the login strategy never ran.
        Assert.Equal(MatchTypeNames.InstallReferrer, outcome.Response!.MatchType);
        Assert.Equal(0, _clicks.CandidateQueries);
    }

    [Fact]
    public async Task Resolve_NullRequest_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await UseCase().ExecuteAsync(null!, Caller, null, TestContext.Current.CancellationToken));

    private async Task<ResolveOutcome> ResolveAsync(ResolveRequestDto request) =>
        await UseCase().ExecuteAsync(
            request,
            Caller,
            IPAddress.Parse("203.0.113.42"),
            TestContext.Current.CancellationToken);

    private ResolveInstall UseCase()
    {
        IOptions<AttributionOptions> options = Options.Create(_options);

        var claimCodes = new ClaimCodeService(_store, _claimCodeGenerator, _clock, _metrics, options);

        var loginKeys = new LoginKeyHasher(Options.Create(new CryptoOptions
        {
            MasterSecret = "unit-test-master-secret-for-login-keys-0123456789",
        }));

        return new ResolveInstall(
            _store,
            _clicks,
            _clickIds,
            claimCodes,
            loginKeys,
            new IpHasher("unit-test-ip-hash-secret-0123456789"u8.ToArray(), TimeSpan.FromHours(24)),
            _outbox,
            _metrics,
            _clock,
            options,
            NullLogger<ResolveInstall>.Instance);
    }

    private static string Referrer(string clickId) =>
        "utm_source=google-play&utm_medium=cpc&" + InstallReferrerParser.ClickIdKey + "=" + clickId;

    private static ClickRecord Click(string clickId, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        OccurredAt = occurredAt,
        TenantId = Tenant,
        LinkId = 77,
        ClickId = clickId,
        DeeplinkPath = "/summer",
    };

    private static DeviceSignalsDto Signals() => new()
    {
        Language = "en",
        Screen = "1170x2532@3",
        TzOffset = 120,
        DeviceModel = "iPhone15,2",
    };

    private static ConsentDto Consented() => new() { Analytics = true, Attribution = true, Ts = Now };

    private static ResolveRequestDto Request(
        string installId = "install-A",
        string platform = "android",
        string? osVersion = "14",
        string? referrer = null,
        string? claimCode = null,
        string? loginKey = null,
        DeviceSignalsDto? signals = null,
        ConsentDto? consent = null,
        bool withoutConsentSignal = false) => new()
        {
            InstallId = installId,
            Platform = platform,
            AppVersion = "1.0.0",
            OsVersion = osVersion,
            Referrer = referrer,
            ClaimCode = claimCode,
            LoginKey = loginKey,
            Signals = signals,

            // A caller that sends no consent object at all is a distinct case from one that sends
            // "attribution: false", and both have to be tested, so the default cannot swallow null.
            Consent = withoutConsentSignal ? null : consent ?? Consented(),
        };

    private static Dictionary<string, string> EvidenceOf(AttributionRecord record)
    {
        Dictionary<string, string>? values =
            JsonSerializer.Deserialize<Dictionary<string, string>>(record.Evidence);

        Assert.NotNull(values);

        return values;
    }

    /// <summary>Captures what was put on the webhook outbox (§B.7.4).</summary>
    private sealed class RecordingOutbox : IWebhookOutbox
    {
        private readonly List<(Guid TenantId, string EventType, string PayloadJson)> _events = [];

        internal List<(Guid TenantId, string EventType, string PayloadJson)> Events => _events;

        public Task EnqueueAsync(Guid tenantId, string eventType, string payloadJson, CancellationToken ct)
        {
            _events.Add((tenantId, eventType, payloadJson));
            return Task.CompletedTask;
        }
    }
}
