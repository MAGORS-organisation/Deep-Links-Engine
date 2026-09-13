using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Dle.Domain.Attribution;
using Dle.Domain.Clients;
using Dle.Domain.Contracts;
using Dle.Domain.Crypto;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Persistence.Repositories;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// What <see cref="ResolveInstall"/> decided.
/// </summary>
/// <param name="Response">The answer to return, or <see langword="null"/> when the request failed
/// with a claim code problem instead.</param>
/// <param name="ClaimCodeError">Why a presented claim code could not be redeemed, which the endpoint
/// turns into an RFC 9457 document rather than a silent <c>none</c> (TC-148).</param>
public sealed record ResolveOutcome(ResolveResponseDto? Response, ClaimCodeStatus? ClaimCodeError);

/// <summary>
/// <c>POST /v1/resolve</c> — hands a freshly installed application the context of the click that
/// led to it (§B.6.2, §B.6.3, §B.7.2, FR-182 to FR-188).
/// </summary>
/// <remarks>
/// <para>
/// The strategies run in the configured order and the first one that matches wins (ADR-008). The
/// order is configuration because it is a product decision: the default is the three deterministic
/// strategies, strongest first, and probabilistic matching is not in it.
/// </para>
/// <para>
/// Three properties are enforced here rather than hoped for.
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Consent is an input, not a filter.</b> Cross-session linking of a click to an install is
///     what §E.6.2 calls <c>full</c> mode and what ePrivacy art. 5(3) puts behind documented
///     consent, so without it no click is looked up at all and the answer is <c>none</c> with the
///     reason recorded. Device signals go further still: the only code that can read them is an
///     instance method of <see cref="ProbabilisticConsent"/>, and without the grant no such
///     instance exists (TC-145, TC-146).
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>One install, one attribution.</b> A repeated call finds the stored decision and returns
///     it unchanged; it never writes a second row (TC-143). The single exception is narrow and
///     deliberate: a stored <c>none</c> may be replaced in place when the caller comes back with
///     deterministic evidence it did not have before, which is what makes "ask for a new claim
///     code" a real remedy (TC-148).
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>One click, one install.</b> The second installation to present the same click identifier
///     is told <c>none</c>. That is enforced by the partial unique index on
///     <c>attributions(click_id)</c> plus the transaction in
///     <see cref="AttributionRepository"/> — not by a check in this class, which could lose a race
///     (TC-144).
///     </description>
///   </item>
/// </list>
/// <para>
/// Every decision, including every refusal, is written with an evidence document naming what
/// decided it. §B.5.3 is blunt about why: when a customer disputes an attribution, that document is
/// the only defence.
/// </para>
/// </remarks>
public sealed partial class ResolveInstall
{
    /// <summary>Confidence of a deterministic strategy. Never approximated (FR-186).</summary>
    private const decimal DeterministicConfidence = 1.00m;

    private readonly IAttributionStore _store;
    private readonly IClickLookup _clicks;
    private readonly IClickIdCodec _clickIds;
    private readonly ClaimCodeService _claimCodes;
    private readonly LoginKeyHasher _loginKeys;
    private readonly IIpHasher _ipHasher;
    private readonly IWebhookOutbox _outbox;
    private readonly AttributionMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly AttributionOptions _options;
    private readonly ILogger<ResolveInstall> _logger;

    /// <summary>
    /// Creates the use case.
    /// </summary>
    /// <param name="store">Storage seam for installations, attributions, claim codes and links.</param>
    /// <param name="clicks">Click stream lookup, always with a bounded time window (§B.6.3).</param>
    /// <param name="clickIds">Click identifier codec; recovers the embedded timestamp and detects tampering.</param>
    /// <param name="claimCodes">Claim code issuance and redemption.</param>
    /// <param name="loginKeys">Account hash used by login reconciliation.</param>
    /// <param name="ipHasher">Address truncation, used only under full consent.</param>
    /// <param name="outbox">Webhook outbox; every successful match is announced.</param>
    /// <param name="metrics">Instruments (§C.6).</param>
    /// <param name="timeProvider">Clock (SHARED-KERNEL §17.2).</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ResolveInstall(
        IAttributionStore store,
        IClickLookup clicks,
        IClickIdCodec clickIds,
        ClaimCodeService claimCodes,
        LoginKeyHasher loginKeys,
        IIpHasher ipHasher,
        IWebhookOutbox outbox,
        AttributionMetrics metrics,
        TimeProvider timeProvider,
        IOptions<AttributionOptions> options,
        ILogger<ResolveInstall> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clicks);
        ArgumentNullException.ThrowIfNull(clickIds);
        ArgumentNullException.ThrowIfNull(claimCodes);
        ArgumentNullException.ThrowIfNull(loginKeys);
        ArgumentNullException.ThrowIfNull(ipHasher);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _clicks = clicks;
        _clickIds = clickIds;
        _claimCodes = claimCodes;
        _loginKeys = loginKeys;
        _ipHasher = ipHasher;
        _outbox = outbox;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves one installation.
    /// </summary>
    /// <param name="request">The validated request body.</param>
    /// <param name="caller">The authenticated tenant and application.</param>
    /// <param name="remoteAddress">Address the call arrived from, used only to derive a truncated
    /// network prefix and only when consent allows. Never stored raw, never logged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, or a claim code failure for the endpoint to report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<ResolveOutcome> ExecuteAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Platform platform = ParsePlatform(request.Platform);

        ConsentSignal? signal = request.Consent is { } consent
            ? new ConsentSignal
            {
                Analytics = consent.Analytics,
                Attribution = consent.Attribution,
                Timestamp = consent.Ts,
                Source = "sdk",
            }
            : null;

        ConsentMode tenantMode = await _store.GetTenantConsentModeAsync(caller.TenantId, cancellationToken);
        ConsentDecision decision = ConsentGate.Evaluate(tenantMode, domainOverride: null, signal);
        IReadOnlyList<AttributionStrategy> order = _options.ResolveOrder();

        // The capability that gates every use of the device signals. Null here means the request's
        // signals object is dropped with the request: nothing below can read it, so nothing below
        // can store it (TC-145, TC-146).
        ProbabilisticConsent? grant = ProbabilisticConsent.TryGrant(_options, order, decision);

        byte[]? loginKeyHash = decision.AllowClickIdLinking && !string.IsNullOrWhiteSpace(request.LoginKey)
            ? _loginKeys.Hash(request.LoginKey)
            : null;

        Install install = await _store.GetOrCreateInstallAsync(
            new Install
            {
                TenantId = caller.TenantId,
                AppId = caller.AppId,
                InstallId = request.InstallId,
                FirstOpenAt = now,
                Platform = PlatformName(platform),
                AppVersion = Clip(request.AppVersion, 64),

                // The referrer is kept verbatim so a disputed attribution can be re-examined
                // against the original evidence — but only when consent allows the linkage it
                // describes. Without that, it is a click identifier we have no basis to keep.
                RawReferrer = decision.AllowClickIdLinking
                    ? Clip(request.Referrer, InstallReferrerParser.MaxReferrerLength)
                    : null,
                LoginKeyHash = loginKeyHash,
                CreatedAt = now,
            },
            cancellationToken);

        if (loginKeyHash is not null)
        {
            await _store.RecordLoginKeyAsync(install.Id, loginKeyHash, cancellationToken);
        }

        AttributionRecord? stored = await _store.FindAttributionByInstallAsync(install.Id, cancellationToken);

        // TC-143. A decision already exists, so this call re-reads it instead of making a new one.
        if (stored is not null && !string.Equals(stored.MatchType, MatchTypeNames.None, StringComparison.Ordinal))
        {
            return new ResolveOutcome(await RespondAsync(stored, cancellationToken), ClaimCodeError: null);
        }

        // Without attribution consent nothing is linked, but the answer is not final: the host
        // application may record consent later, and the deterministic evidence - the install
        // referrer above all - stays valid for the referrer window. The window travels as
        // expires_in, so an SDK re-asks once consent arrives instead of caching "none" forever.
        StrategyOutcome outcome = decision.AllowClickIdLinking
            ? await RunStrategiesAsync(request, caller, order, grant, install, now, remoteAddress, cancellationToken)
            : new StrategyOutcome(
                AttributionResult.NoMatch(AttributionReasons.ConsentMissing),
                MatchedClickId: null,
                WindowSeconds: ConsentRetryWindowSeconds(),
                ClaimCode: null,
                ClaimCodeError: null);

        if (outcome.ClaimCodeError is { } claimCodeError)
        {
            return new ResolveOutcome(Response: null, claimCodeError);
        }

        AttributionResult result = Enrich(outcome.Result, decision);

        return new ResolveOutcome(
            await PersistAsync(result, outcome, stored, install, caller, now, cancellationToken),
            ClaimCodeError: null);
    }

    /// <summary>Runs the configured strategies in order and returns the first match.</summary>
    private async Task<StrategyOutcome> RunStrategiesAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        IReadOnlyList<AttributionStrategy> order,
        ProbabilisticConsent? grant,
        Install install,
        DateTimeOffset now,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken)
    {
        var refusals = new AttributionEvidence();

        foreach (AttributionStrategy strategy in order)
        {
            StrategyOutcome attempt = strategy switch
            {
                AttributionStrategy.InstallReferrer =>
                    await TryInstallReferrerAsync(request, caller, now, cancellationToken),
                AttributionStrategy.Login =>
                    await TryLoginAsync(request, caller, now, cancellationToken),
                AttributionStrategy.ClaimCode =>
                    await TryClaimCodeAsync(request, caller, cancellationToken),
                AttributionStrategy.Probabilistic =>
                    await TryProbabilisticAsync(request, caller, install, grant, now, remoteAddress, cancellationToken),
                _ => Refused(strategy, AttributionReasons.NoStrategyMatched),
            };

            if (attempt.ClaimCodeError is not null)
            {
                return attempt;
            }

            if (attempt.Result.Matched)
            {
                return attempt;
            }

            RecordRefusal(refusals, strategy, attempt.Result);
        }

        AttributionResult none = AttributionResult.NoMatch(AttributionReasons.NoStrategyMatched);

        return new StrategyOutcome(
            none with { Evidence = Merge(none.Evidence, refusals.ToDictionary()) },
            MatchedClickId: null,
            WindowSeconds: null,
            ClaimCode: null,
            ClaimCodeError: null);
    }

    /// <summary>
    /// S1 — the Android Play Install Referrer (§A.2.4, §B.6.2, FR-181, FR-182).
    /// </summary>
    /// <remarks>
    /// The click identifier carries its own encrypted timestamp, so the lookup can bound
    /// <c>occurred_at</c> and the planner can prune the partitions of <c>click_events</c>. Without
    /// that bound the query touches every partition and its cost grows linearly with retention —
    /// the silent debt §B.6.3 warns about, which only becomes visible half a year after launch.
    /// </remarks>
    private async Task<StrategyOutcome> TryInstallReferrerAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const AttributionStrategy strategy = AttributionStrategy.InstallReferrer;

        if (!InstallReferrerParser.TryGetClickId(request.Referrer, out string clickId))
        {
            // Not an error. An organic install has no dl_cid and the application simply continues
            // its normal onboarding (TC-142).
            return Refused(strategy, AttributionReasons.NoClickId);
        }

        if (!_clickIds.TryDecode(clickId, out DateTimeOffset occurredAt, out _))
        {
            // The identifier is authenticated, so a failed decode is a mutated identifier rather
            // than an unlucky one. It is recorded as tampered and never matched (TC-167, T-05).
            _metrics.Tampered(AttributionStrategyNames.InstallReferrer);
            LogTamperedClickId(_logger, caller.TenantId, clickId.Length);

            return new StrategyOutcome(
                AttributionResult.NoMatch(AttributionReasons.TamperedClickId) with
                {
                    Evidence = Merge(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [AttributionEvidence.ReasonKey] = AttributionReasons.TamperedClickId,
                        },
                        new AttributionEvidence(strategy).With(AttributionEvidence.TamperedKey, "true").ToDictionary()),
                },
                MatchedClickId: null,
                WindowSeconds: null,
                ClaimCode: null,
                ClaimCodeError: null);
        }

        TimeSpan age = now - occurredAt;

        if (age > TimeSpan.FromDays(_options.MaxReferrerAgeDays))
        {
            return Refused(strategy, AttributionReasons.ReferrerTooOld);
        }

        TimeSpan half = TimeSpan.FromMinutes(_options.ClickWindowMinutes);

        ClickRecord? click = await _clicks.FindByClickIdAsync(
            clickId,
            occurredAt - half,
            occurredAt + half,
            cancellationToken);

        if (click is null)
        {
            return Refused(strategy, AttributionReasons.ClickNotFound);
        }

        if (click.TenantId != caller.TenantId)
        {
            // A click that belongs to somebody else is answered as "no match", never as a refusal:
            // a 403 would confirm the identifier exists (SHARED-KERNEL §17.7, TC-166).
            return Refused(strategy, AttributionReasons.ForeignTenant);
        }

        AttributionEvidence evidence = new AttributionEvidence(strategy)
            .With(AttributionEvidence.ClickOccurredAtKey, click.OccurredAt)
            .With(AttributionEvidence.ElapsedMinutesKey, (long)age.TotalMinutes);

        return Matched(
            MatchType.InstallReferrer,
            DeterministicConfidence,
            click,
            evidence,
            (int)TimeSpan.FromDays(_options.MaxReferrerAgeDays).TotalSeconds);
    }

    /// <summary>
    /// S2 — reconciliation of an anonymous web session with a signed-in account (FR-185).
    /// </summary>
    /// <remarks>
    /// The anonymous web identifier is the click. When the visitor was signed in on the web, the
    /// edge records the keyed account hash in <c>click_events.extra</c> under
    /// <see cref="LoginKeyHasher.ClickExtraKey"/>; when the same account signs in inside the
    /// application, the two halves meet here. Both sides are keyed hashes, so neither storage nor
    /// this comparison ever sees an account identifier.
    /// </remarks>
    private async Task<StrategyOutcome> TryLoginAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const AttributionStrategy strategy = AttributionStrategy.Login;

        if (string.IsNullOrWhiteSpace(request.LoginKey))
        {
            return Refused(strategy, AttributionReasons.NoLoginKey);
        }

        byte[] hash = _loginKeys.Hash(request.LoginKey);
        TimeSpan window = TimeSpan.FromHours(_options.LoginWindowHours);

        IReadOnlyList<ClickRecord> candidates = await _clicks.FindCandidatesAsync(
            caller.TenantId,
            now - window,
            now,
            ipPrefix: null,
            osFamily: null,
            cancellationToken);

        foreach (ClickRecord candidate in candidates)
        {
            if (candidate.Extra is null ||
                !candidate.Extra.TryGetValue(LoginKeyHasher.ClickExtraKey, out string? recorded) ||
                !LoginKeyHasher.Matches(hash, recorded))
            {
                continue;
            }

            AttributionEvidence evidence = new AttributionEvidence(strategy)
                .With(AttributionEvidence.ClickOccurredAtKey, candidate.OccurredAt)
                .With(AttributionEvidence.ElapsedMinutesKey, (long)(now - candidate.OccurredAt).TotalMinutes)
                .With(AttributionEvidence.CandidatesKey, candidates.Count);

            return Matched(
                MatchType.Login,
                DeterministicConfidence,
                candidate,
                evidence,
                (int)window.TotalSeconds);
        }

        return Refused(strategy, AttributionReasons.LoginNotFound);
    }

    /// <summary>
    /// S3 — the six characters the user read off the interstitial page (FR-184, TC-148).
    /// </summary>
    /// <remarks>
    /// A failure here does not fall through to the next strategy. The user deliberately typed a
    /// code, so an expired or unknown one is answered with a problem document that says so and
    /// says whether asking for a new code helps; quietly degrading to a probabilistic guess would
    /// be the wrong answer to a deterministic question.
    /// </remarks>
    private async Task<StrategyOutcome> TryClaimCodeAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        CancellationToken cancellationToken)
    {
        const AttributionStrategy strategy = AttributionStrategy.ClaimCode;

        if (string.IsNullOrWhiteSpace(request.ClaimCode))
        {
            return Refused(strategy, AttributionReasons.NoClaimCode);
        }

        ClaimCodeRedemption redemption = await _claimCodes.InspectAsync(request.ClaimCode, cancellationToken);

        if (redemption.Status != ClaimCodeStatus.Valid || redemption.Record is not { } record)
        {
            return new StrategyOutcome(
                AttributionResult.NoMatch(AttributionReasons.NoClaimCode),
                MatchedClickId: null,
                WindowSeconds: null,
                ClaimCode: null,
                redemption.Status);
        }

        if (record.TenantId != caller.TenantId)
        {
            return Refused(strategy, AttributionReasons.ForeignTenant);
        }

        AttributionEvidence evidence = new AttributionEvidence(strategy)
            .With(AttributionEvidence.ClickOccurredAtKey, record.CreatedAt)
            .With(AttributionEvidence.WindowSecondsKey, (long)_options.ClaimCode.Ttl.TotalSeconds);

        var result = new AttributionResult
        {
            Matched = true,
            MatchType = MatchType.ClaimCode,
            Confidence = DeterministicConfidence,
            ClickId = record.ClickId,
            LinkId = record.LinkId,
            Evidence = evidence.ToDictionary(),
        };

        return new StrategyOutcome(
            result,
            record.ClickId,
            (int)_options.ClaimCode.Ttl.TotalSeconds,
            record,
            ClaimCodeError: null);
    }

    /// <summary>
    /// S4 — probabilistic matching (§A.2.5, ADR-008). Off by default, consent gated, time bounded.
    /// </summary>
    /// <remarks>
    /// Past the window the answer is <c>none</c>, never a weak match. That is enforced twice: the
    /// candidate query only asks for clicks inside the window, and
    /// <see cref="ProbabilisticScorer"/> returns exactly zero once the elapsed time reaches it
    /// (TC-147). The default window is sixty minutes rather than the seven days commercial vendors
    /// use, because beyond twenty-four hours a fingerprint match is more likely wrong than right.
    /// </remarks>
    private async Task<StrategyOutcome> TryProbabilisticAsync(
        ResolveRequestDto request,
        SdkCaller caller,
        Install install,
        ProbabilisticConsent? grant,
        DateTimeOffset now,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken)
    {
        const AttributionStrategy strategy = AttributionStrategy.Probabilistic;

        if (grant is null)
        {
            // The module is off, is not in the configured order, or consent was not given. The
            // request's signals object was never read and is discarded with the request body.
            return Refused(strategy, AttributionReasons.ProbabilisticUnavailable);
        }

        string? ipPrefix = remoteAddress is null ? null : _ipHasher.Prefix(remoteAddress);
        DeviceSignals? signals = grant.Read(request.Signals, ipPrefix, request.OsVersion);

        if (signals is null)
        {
            return Refused(strategy, AttributionReasons.NoSignals);
        }

        IReadOnlyList<ClickRecord> candidates = await _clicks.FindCandidatesAsync(
            caller.TenantId,
            now - grant.Window,
            now,
            signals.IpPrefix,
            OsFamilyOf(install.Platform),
            cancellationToken);

        if (candidates.Count == 0)
        {
            return Refused(strategy, AttributionReasons.OutsideWindow);
        }

        ClickRecord? best = null;
        decimal bestScore = 0m;
        decimal runnerUp = 0m;

        foreach (ClickRecord candidate in candidates)
        {
            decimal score = ProbabilisticScorer.Score(
                signals,
                ToSignals(candidate),
                now - candidate.OccurredAt,
                grant.Window);

            if (score > bestScore)
            {
                runnerUp = bestScore;
                bestScore = score;
                best = candidate;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
            }
        }

        if (best is null || bestScore < grant.MinConfidence)
        {
            return Refused(
                strategy,
                bestScore <= 0m ? AttributionReasons.OutsideWindow : AttributionReasons.BelowMinConfidence,
                evidence => evidence
                    .With(AttributionEvidence.CandidatesKey, candidates.Count)
                    .With(AttributionEvidence.MinConfidenceKey, grant.MinConfidence));
        }

        DeviceSignals bestSignals = ToSignals(best);

        AttributionEvidence matchEvidence = new AttributionEvidence(strategy)
            .With(AttributionEvidence.ConsentKey, grant.ConsentReason)
            .With(AttributionEvidence.ClickOccurredAtKey, best.OccurredAt)
            .With(AttributionEvidence.ElapsedMinutesKey, (long)(now - best.OccurredAt).TotalMinutes)
            .With(AttributionEvidence.WindowSecondsKey, (long)grant.Window.TotalSeconds)
            .With(AttributionEvidence.CandidatesKey, candidates.Count)
            .With(AttributionEvidence.MinConfidenceKey, grant.MinConfidence)
            .With(AttributionEvidence.RunnerUpKey, runnerUp);

        DescribeSignals(matchEvidence, signals, bestSignals);

        return Matched(
            MatchType.Probabilistic,
            bestScore,
            best,
            matchEvidence,
            (int)grant.Window.TotalSeconds);
    }

    /// <summary>
    /// Writes the decision, honouring both uniqueness rules, and announces a new match.
    /// </summary>
    private async Task<ResolveResponseDto> PersistAsync(
        AttributionResult result,
        StrategyOutcome outcome,
        AttributionRecord? stored,
        Install install,
        SdkCaller caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AttributionRecord record = ToRecord(result, outcome, install, caller, now);

        if (stored is not null)
        {
            // The installation already carries a "none". Only strictly new deterministic evidence
            // may replace it, and it replaces the same row rather than adding one (TC-143, TC-148).
            if (!result.Matched || result.MatchType == MatchType.Probabilistic)
            {
                return await RespondAsync(stored, cancellationToken);
            }

            AttributionWriteResult upgrade =
                await _store.TryUpgradeNoneAttributionAsync(record, cancellationToken);

            if (upgrade.Outcome != AttributionOutcome.Created || upgrade.Record is null)
            {
                await SpendClaimCodeAsync(outcome, cancellationToken);
                return await RespondAsync(upgrade.Record ?? stored, cancellationToken);
            }

            await SpendClaimCodeAsync(outcome, cancellationToken);
            await AnnounceAsync(upgrade.Record, install, caller, cancellationToken);

            record = upgrade.Record;
            record.Evidence = MergeEvidenceJson(record.Evidence, AttributionEvidence.UpgradedFromKey, MatchTypeNames.None);

            _metrics.Decision(record.MatchType, record.Confidence);

            return await RespondAsync(record, cancellationToken);
        }

        AttributionWriteResult write = await _store.TryCreateAttributionAsync(record, cancellationToken);

        await SpendClaimCodeAsync(outcome, cancellationToken);

        switch (write.Outcome)
        {
            case AttributionOutcome.Created when write.Record is not null:
                _metrics.Decision(write.Record.MatchType, write.Record.Confidence);

                if (result.Matched)
                {
                    await AnnounceAsync(write.Record, install, caller, cancellationToken);
                }

                return await RespondAsync(write.Record, cancellationToken);

            case AttributionOutcome.AlreadyAttributed when write.Record is not null:
                // Another call for the same installation won the race. Its answer is the answer.
                return await RespondAsync(write.Record, cancellationToken);

            case AttributionOutcome.ClickAlreadyClaimed:
                // TC-144: one click is worth one install, and the first one wins. The loser is told
                // "none" and the refusal is recorded with the click identifier it wanted, in the
                // evidence document rather than in the indexed column.
                return await RecordLostClaimAsync(record, install, caller, now, cancellationToken);

            case AttributionOutcome.Created:
            case AttributionOutcome.AlreadyAttributed:
            default:
                // No row came back with an outcome that promises one. Fail closed: report the
                // install as organic rather than inventing a match (SHARED-KERNEL §17.9).
                LogWriteWithoutRecord(_logger, write.Outcome.ToString());
                return NoMatchResponse();
        }
    }

    /// <summary>Records the "none" that a losing double claim earns (TC-144).</summary>
    private async Task<ResolveResponseDto> RecordLostClaimAsync(
        AttributionRecord attempted,
        Install install,
        SdkCaller caller,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var evidence = new AttributionEvidence()
            .With(AttributionEvidence.ReasonKey, AttributionReasons.ClickAlreadyClaimed)
            .With(AttributionEvidence.StrategyKey, attempted.MatchType);

        if (attempted.ClickId is { Length: > 0 } refused)
        {
            evidence.With("refused_click_id", refused);
        }

        var none = new AttributionRecord
        {
            TenantId = caller.TenantId,
            InstallId = install.Id,

            // Null, and it has to be: the partial unique index on attributions(click_id) is what
            // just refused this row, so recording the identifier here would refuse it again.
            ClickId = null,
            LinkId = null,
            MatchType = MatchTypeNames.None,
            Confidence = 0m,
            MatchedAt = now,
            Evidence = AttributionEvidence.ToJson(evidence.ToDictionary()),
        };

        AttributionWriteResult write = await _store.TryCreateAttributionAsync(none, cancellationToken);

        if (write is { Outcome: AttributionOutcome.AlreadyAttributed, Record: not null })
        {
            return await RespondAsync(write.Record, cancellationToken);
        }

        _metrics.Decision(MatchTypeNames.None, 0m);

        return NoMatchResponse();
    }

    /// <summary>Spends the claim code that produced a decision, whatever the decision was.</summary>
    private async Task SpendClaimCodeAsync(StrategyOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.ClaimCode is { } record)
        {
            await _claimCodes.ConsumeAsync(record, cancellationToken);
        }
    }

    /// <summary>Puts <c>attribution.created</c> on the webhook outbox (§B.7.4).</summary>
    private async Task AnnounceAsync(
        AttributionRecord record,
        Install install,
        SdkCaller caller,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install_id"] = install.InstallId,
            ["app_id"] = caller.AppId.ToString(),
            ["match_type"] = record.MatchType,
            ["confidence"] = record.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
        };

        if (record.ClickId is { Length: > 0 } clickId)
        {
            data["click_id"] = clickId;
        }

        if (record.LinkId is { } linkId)
        {
            data["link_id"] = linkId.ToString(CultureInfo.InvariantCulture);
        }

        var payload = new WebhookPayload
        {
            Event = WebhookEvents.AttributionCreated,
            Id = record.Id.ToString(),
            OccurredAt = record.MatchedAt,
            Data = new ReadOnlyDictionary<string, string>(data),
        };

        await _outbox.EnqueueAsync(
            caller.TenantId,
            WebhookEvents.AttributionCreated,
            JsonSerializer.Serialize(payload, AttributionJsonContext.Default.WebhookPayload),
            cancellationToken);
    }

    /// <summary>Turns a stored decision into the response body.</summary>
    private async Task<ResolveResponseDto> RespondAsync(AttributionRecord record, CancellationToken cancellationToken)
    {
        MatchType matchType = MatchTypeNames.Parse(record.MatchType);

        if (matchType == MatchType.None)
        {
            return NoMatchResponse(RemainingWindowSeconds(record, _timeProvider.GetUtcNow()));
        }

        AttributedLink? link = record.LinkId is { } linkId
            ? await _store.FindLinkAsync(linkId, cancellationToken)
            : null;

        return new ResolveResponseDto
        {
            Matched = true,
            MatchType = record.MatchType,
            Confidence = record.Confidence,
            ClickId = record.ClickId,
            Link = link is null
                ? null
                : new ResolveLinkDto
                {
                    Id = link.Id.ToString(CultureInfo.InvariantCulture),
                    DeeplinkPath = DeeplinkPathOf(record, link),
                    Campaign = link.Campaign,
                    Title = link.Title,
                },

            // Parameters come from the link, never from the referrer the client presented. A value
            // the caller supplied must not come back as engine output, and using only stored data
            // is also what makes a repeated call return byte-identical parameters (TC-143).
            Params = link?.Parameters ?? ReadOnlyDictionary<string, string>.Empty,
            ExpiresIn = 0,
        };
    }

    /// <summary>The answer for an install nothing matched: organic, and not an error (TC-142).</summary>
    private static ResolveResponseDto NoMatchResponse(int expiresIn = 0) => new()
    {
        Matched = false,
        MatchType = MatchTypeNames.None,
        Confidence = 0m,
        ExpiresIn = expiresIn,
    };

    /// <summary>
    /// How long a no-match answer given for want of consent may still change: the install
    /// referrer window, after which the deterministic evidence is gone anyway.
    /// </summary>
    private int ConsentRetryWindowSeconds() =>
        (int)Math.Min(int.MaxValue, TimeSpan.FromDays(_options.MaxReferrerAgeDays).TotalSeconds);

    /// <summary>
    /// What is left of a stored no-match answer's window, so a repeat call gets the same
    /// deadline rather than a fresh one. Zero - final - when the record carries no window.
    /// </summary>
    private static int RemainingWindowSeconds(AttributionRecord record, DateTimeOffset now)
    {
        if (record.WindowSeconds is not int window || window <= 0)
        {
            return 0;
        }

        double elapsed = (now - record.MatchedAt).TotalSeconds;
        return (int)Math.Clamp(window - elapsed, 0, int.MaxValue);
    }

    /// <summary>Builds the row for a decision.</summary>
    private static AttributionRecord ToRecord(
        AttributionResult result,
        StrategyOutcome outcome,
        Install install,
        SdkCaller caller,
        DateTimeOffset now) => new()
        {
            TenantId = caller.TenantId,
            InstallId = install.Id,
            ClickId = result.Matched ? result.ClickId : null,
            LinkId = result.Matched ? result.LinkId : null,
            MatchType = MatchTypeNames.From(result.MatchType),
            Confidence = result.Confidence,
            MatchedAt = now,
            WindowSeconds = outcome.WindowSeconds,
            Evidence = AttributionEvidence.ToJson(result.Evidence),
        };

    /// <summary>Adds the consent reason to whatever the strategy recorded.</summary>
    private static AttributionResult Enrich(AttributionResult result, ConsentDecision decision)
    {
        var evidence = new Dictionary<string, string>(result.Evidence, StringComparer.Ordinal)
        {
            [AttributionEvidence.ConsentKey] = decision.Reason,
        };

        return result with { Evidence = new ReadOnlyDictionary<string, string>(evidence) };
    }

    /// <summary>Builds the outcome of a matching strategy.</summary>
    private static StrategyOutcome Matched(
        MatchType matchType,
        decimal confidence,
        ClickRecord click,
        AttributionEvidence evidence,
        int? windowSeconds)
    {
        var result = new AttributionResult
        {
            Matched = true,
            MatchType = matchType,
            Confidence = confidence,
            ClickId = click.ClickId,
            LinkId = click.LinkId,
            DeeplinkPath = click.DeeplinkPath,
            Evidence = evidence.ToDictionary(),
        };

        return new StrategyOutcome(result, click.ClickId, windowSeconds, ClaimCode: null, ClaimCodeError: null);
    }

    /// <summary>Builds the outcome of a strategy that did not match.</summary>
    private static StrategyOutcome Refused(
        AttributionStrategy strategy,
        string reason,
        Action<AttributionEvidence>? describe = null)
    {
        var evidence = new AttributionEvidence(strategy).With(AttributionEvidence.ReasonKey, reason);
        describe?.Invoke(evidence);

        AttributionResult result = AttributionResult.NoMatch(reason);

        return new StrategyOutcome(
            result with { Evidence = Merge(result.Evidence, evidence.ToDictionary()) },
            MatchedClickId: null,
            WindowSeconds: null,
            ClaimCode: null,
            ClaimCodeError: null);
    }

    /// <summary>Folds one strategy's refusal into the combined evidence of a "none".</summary>
    private static void RecordRefusal(AttributionEvidence combined, AttributionStrategy strategy, AttributionResult result)
    {
        string reason = result.Evidence.TryGetValue(AttributionEvidence.ReasonKey, out string? value)
            ? value
            : AttributionReasons.NoStrategyMatched;

        combined.With(AttributionEvidence.SignalKeyPrefix.Length > 0 ? StrategyReasonKey(strategy) : reason, reason);

        if (result.Evidence.TryGetValue(AttributionEvidence.TamperedKey, out string? tampered))
        {
            combined.With(AttributionEvidence.TamperedKey, tampered);
        }
    }

    /// <summary>Evidence key under which one strategy's refusal is recorded.</summary>
    private static string StrategyReasonKey(AttributionStrategy strategy) => strategy switch
    {
        AttributionStrategy.InstallReferrer => "refused." + AttributionStrategyNames.InstallReferrer,
        AttributionStrategy.Login => "refused." + AttributionStrategyNames.Login,
        AttributionStrategy.ClaimCode => "refused." + AttributionStrategyNames.ClaimCode,
        AttributionStrategy.Probabilistic => "refused." + AttributionStrategyNames.Probabilistic,
        _ => "refused.unknown",
    };

    /// <summary>Names the signals that agreed, with the weight each contributed.</summary>
    private static void DescribeSignals(AttributionEvidence evidence, DeviceSignals candidate, DeviceSignals click)
    {
        if (Same(candidate.IpPrefix, click.IpPrefix))
        {
            evidence.ProbabilisticSignal("ip_prefix", ProbabilisticScorer.IpPrefixWeight);
        }

        if (Same(candidate.OsVersion, click.OsVersion))
        {
            evidence.ProbabilisticSignal("os_version", ProbabilisticScorer.OsVersionWeight);
        }

        if (Same(candidate.Language, click.Language))
        {
            evidence.ProbabilisticSignal("language", ProbabilisticScorer.LanguageWeight);
        }

        if (candidate.TimezoneOffsetMinutes is { } left && click.TimezoneOffsetMinutes is { } right && left == right)
        {
            evidence.ProbabilisticSignal("timezone", ProbabilisticScorer.TimezoneWeight);
        }

        if (Same(candidate.Screen, click.Screen))
        {
            evidence.ProbabilisticSignal("screen", ProbabilisticScorer.ScreenWeight);
        }
    }

    /// <summary>Reads the signals recorded with a click.</summary>
    /// <remarks>
    /// These values were written only under full consent, so an absent value is the normal case
    /// rather than a fault. Reading them here does not need the capability that reading the
    /// request's signals needs: they are already stored, and the decision to store them was taken
    /// at click time by the edge's own consent gate.
    /// </remarks>
    private static DeviceSignals ToSignals(ClickRecord click)
    {
        string? screen = null;
        int? timezone = null;

        if (click.Extra is { } extra)
        {
            if (extra.TryGetValue(ClickExtraKeys.Screen, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                screen = value;
            }

            if (extra.TryGetValue(ClickExtraKeys.TimezoneOffsetMinutes, out string? offset) &&
                int.TryParse(offset, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                timezone = parsed;
            }
        }

        return new DeviceSignals
        {
            Language = click.Language,
            Screen = screen,
            TimezoneOffsetMinutes = timezone,
            OsVersion = click.OsVersion,
            IpPrefix = click.IpPrefix,
        };
    }

    /// <summary>Prefers the path resolved for the click, falling back to the link's own.</summary>
    private static string? DeeplinkPathOf(AttributionRecord record, AttributedLink link) =>
        link.DeeplinkPath ?? (record.LinkId == link.Id ? null : null);

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in second)
        {
            merged[entry.Key] = entry.Value;
        }

        return new ReadOnlyDictionary<string, string>(merged);
    }

    private static string MergeEvidenceJson(string evidenceJson, string key, string value)
    {
        Dictionary<string, string>? values = null;

        try
        {
            values = JsonSerializer.Deserialize(evidenceJson, DleDomainJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException)
        {
            // Explicit default: unreadable evidence is replaced rather than lost silently, and the
            // replacement still names why the row changed.
            values = null;
        }

        values ??= new Dictionary<string, string>(StringComparer.Ordinal);
        values[key] = value;

        return JsonSerializer.Serialize(values, DleDomainJsonContext.Default.DictionaryStringString);
    }

    /// <summary>Maps the platform string of a request onto the domain enumeration, failing closed.</summary>
    private static Platform ParsePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Platform.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "ios" => Platform.Ios,
            "android" => Platform.Android,
            "desktop" => Platform.Desktop,
            "other" => Platform.Other,
            _ => Platform.Unknown,
        };
    }

    /// <summary>Stored spelling of a platform.</summary>
    private static string PlatformName(Platform platform) => platform switch
    {
        Platform.Ios => "ios",
        Platform.Android => "android",
        Platform.Desktop => "desktop",
        Platform.Other => "other",
        _ => "unknown",
    };

    /// <summary>Operating system family used to narrow the probabilistic candidate query.</summary>
    private static string? OsFamilyOf(string platform) => platform switch
    {
        "ios" => "iOS",
        "android" => "Android",
        _ => null,
    };

    private static string? Clip(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    /// <summary>The result of one strategy, plus what the persistence step needs from it.</summary>
    /// <param name="Result">The decision.</param>
    /// <param name="MatchedClickId">Click the strategy matched, when it matched one.</param>
    /// <param name="WindowSeconds">Width of the window that was in force, for the stored row.</param>
    /// <param name="ClaimCode">Claim code to spend once the write has been attempted.</param>
    /// <param name="ClaimCodeError">Why a presented claim code could not be used.</param>
    private sealed record StrategyOutcome(
        AttributionResult Result,
        string? MatchedClickId,
        int? WindowSeconds,
        ClaimCodeRecord? ClaimCode,
        ClaimCodeStatus? ClaimCodeError);

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Warning,
        Message = "A click identifier presented by tenant {TenantId} failed its integrity check and was recorded as tampered ({Length} characters).")]
    private static partial void LogTamperedClickId(ILogger logger, Guid tenantId, int length);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Error,
        Message = "The attribution store reported outcome {Outcome} without returning a row; the install was reported as organic.")]
    private static partial void LogWriteWithoutRecord(ILogger logger, string outcome);
}

/// <summary>Webhook event types this module produces (§B.7.4).</summary>
public static class WebhookEvents
{
    /// <summary>An installation was attributed to a click.</summary>
    public const string AttributionCreated = "attribution.created";
}
