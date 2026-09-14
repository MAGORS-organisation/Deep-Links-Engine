using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using Dle.Domain.Attribution;
using Dle.Domain.Serialization;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Builds the document written to <c>attributions.evidence</c>.
/// </summary>
/// <remarks>
/// <para>
/// §B.5.3 puts it plainly: when a customer disputes an attribution — "why did you credit this
/// install to that campaign?" — this column is the only defence. So the evidence is assembled at the
/// moment of the decision, from the values the decision actually used, and it is written in the same
/// transaction as the attribution rather than logged somewhere alongside it.
/// </para>
/// <para>
/// What never goes in: raw device signals, the full referrer, the caller's address. A dispute is
/// answered by naming which signals agreed and what each contributed, which is what
/// <see cref="ProbabilisticSignal"/> records; it is not answered by keeping a fingerprint on file
/// (§E.6.3, SHARED-KERNEL §17.5).
/// </para>
/// </remarks>
public sealed class AttributionEvidence
{
    /// <summary>Strategy that produced the decision.</summary>
    public const string StrategyKey = "strategy";

    /// <summary>Machine readable reason, present on every negative decision.</summary>
    public const string ReasonKey = "reason";

    /// <summary>Consent gate reason code that applied to the request.</summary>
    public const string ConsentKey = "consent";

    /// <summary>Instant the matched click occurred, ISO 8601 in UTC.</summary>
    public const string ClickOccurredAtKey = "click_occurred_at";

    /// <summary>Minutes between the matched click and the resolve call.</summary>
    public const string ElapsedMinutesKey = "elapsed_minutes";

    /// <summary>Width of the window that was in force, in seconds.</summary>
    public const string WindowSecondsKey = "window_seconds";

    /// <summary>Number of candidate clicks the probabilistic scorer considered.</summary>
    public const string CandidatesKey = "candidates";

    /// <summary>Confidence of the runner-up candidate, so a close call is visible afterwards.</summary>
    public const string RunnerUpKey = "runner_up_confidence";

    /// <summary>Minimum confidence that was in force.</summary>
    public const string MinConfidenceKey = "min_confidence";

    /// <summary>Prefix of the per-signal contributions, for example <c>signal.ip_prefix</c>.</summary>
    public const string SignalKeyPrefix = "signal.";

    /// <summary>Set when a previously stored <c>none</c> decision was replaced by a deterministic one.</summary>
    public const string UpgradedFromKey = "upgraded_from";

    /// <summary>Whether the click identifier presented in the referrer failed its integrity check.</summary>
    public const string TamperedKey = "tampered";

    /// <summary>URL that opened the application, for a direct open.</summary>
    public const string OpenedUrlKey = "opened_url";

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Creates an empty evidence document.</summary>
    public AttributionEvidence()
    {
    }

    /// <summary>Creates an evidence document that already names the strategy under evaluation.</summary>
    /// <param name="strategy">The strategy the decision belongs to.</param>
    public AttributionEvidence(AttributionStrategy strategy)
    {
        _values[StrategyKey] = Name(strategy);
    }

    /// <summary>Records a plain value.</summary>
    /// <param name="key">Key, in snake_case.</param>
    /// <param name="value">Value; a <see langword="null"/> or blank value is ignored.</param>
    /// <returns>The same document, so calls can be chained.</returns>
    public AttributionEvidence With(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!string.IsNullOrWhiteSpace(value))
        {
            _values[key] = value;
        }

        return this;
    }

    /// <summary>Records an integral value.</summary>
    /// <param name="key">Key, in snake_case.</param>
    /// <param name="value">Value, formatted with the invariant culture.</param>
    /// <returns>The same document, so calls can be chained.</returns>
    public AttributionEvidence With(string key, long value) =>
        With(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Records a decimal value with two decimal places.</summary>
    /// <param name="key">Key, in snake_case.</param>
    /// <param name="value">Value, formatted with the invariant culture.</param>
    /// <returns>The same document, so calls can be chained.</returns>
    public AttributionEvidence With(string key, decimal value) =>
        With(key, value.ToString("0.00", CultureInfo.InvariantCulture));

    /// <summary>Records an instant in ISO 8601, UTC.</summary>
    /// <param name="key">Key, in snake_case.</param>
    /// <param name="value">The instant.</param>
    /// <returns>The same document, so calls can be chained.</returns>
    public AttributionEvidence With(string key, DateTimeOffset value) =>
        With(key, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Records that one probabilistic signal agreed and what it was worth.</summary>
    /// <param name="signal">Signal name, for example <c>ip_prefix</c>.</param>
    /// <param name="weight">Weight the scorer assigns it.</param>
    /// <returns>The same document, so calls can be chained.</returns>
    public AttributionEvidence ProbabilisticSignal(string signal, decimal weight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);

        return With(SignalKeyPrefix + signal, weight);
    }

    /// <summary>Renders the document as the dictionary carried by <see cref="AttributionResult"/>.</summary>
    /// <returns>A snapshot; later changes to this builder do not affect it.</returns>
    public IReadOnlyDictionary<string, string> ToDictionary() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_values, StringComparer.Ordinal));

    /// <summary>Serializes an evidence dictionary for the <c>jsonb</c> column.</summary>
    /// <param name="evidence">The evidence to serialize.</param>
    /// <returns>A JSON object. Serialization goes through the source generated context, never
    /// reflection (SHARED-KERNEL §17.3).</returns>
    public static string ToJson(IReadOnlyDictionary<string, string> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        Dictionary<string, string> document = evidence as Dictionary<string, string>
            ?? new Dictionary<string, string>(evidence, StringComparer.Ordinal);

        return JsonSerializer.Serialize(document, DleDomainJsonContext.Default.DictionaryStringString);
    }

    /// <summary>Maps a strategy onto the name recorded in the evidence.</summary>
    /// <param name="strategy">The strategy.</param>
    /// <returns>The configuration name, or <c>unknown</c>.</returns>
    private static string Name(AttributionStrategy strategy) => strategy switch
    {
        AttributionStrategy.InstallReferrer => AttributionStrategyNames.InstallReferrer,
        AttributionStrategy.Login => AttributionStrategyNames.Login,
        AttributionStrategy.ClaimCode => AttributionStrategyNames.ClaimCode,
        AttributionStrategy.Probabilistic => AttributionStrategyNames.Probabilistic,
        _ => "unknown",
    };
}

/// <summary>
/// Stable machine readable reasons recorded when nothing matched.
/// </summary>
/// <remarks>
/// "No match" is an answer that has to be explainable too: an integrator debugging why installs are
/// coming back organic needs to tell "the referrer had no click identifier" apart from "another
/// installation already claimed that click". These strings appear in <c>attributions.evidence</c>
/// and in the negative <see cref="AttributionResult"/>, so they are part of the contract.
/// </remarks>
public static class AttributionReasons
{
    /// <summary>
    /// The consent gate refused to link a click to this installation. Deferred matching is
    /// cross-session linking, which §E.6.2 places in the <c>full</c> mode and ePrivacy art. 5(3)
    /// places behind documented consent. An operator wondering why every install comes back
    /// organic finds this reason in the evidence.
    /// </summary>
    public const string ConsentMissing = "consent_missing";

    /// <summary>The referrer carried no <c>dl_cid</c>: an organic install (TC-142).</summary>
    public const string NoClickId = "no_click_id";

    /// <summary>The click identifier failed its integrity check (TC-167, T-05).</summary>
    public const string TamperedClickId = "tampered_click_id";

    /// <summary>The click identifier decoded but no such click exists in the pruned window.</summary>
    public const string ClickNotFound = "click_not_found";

    /// <summary>The referrer is older than the platform keeps them, so it is a replay.</summary>
    public const string ReferrerTooOld = "referrer_too_old";

    /// <summary>The click belongs to another tenant. Answered as no match, never as a refusal.</summary>
    public const string ForeignTenant = "foreign_tenant";

    /// <summary>Another installation already claimed that click (TC-144).</summary>
    public const string ClickAlreadyClaimed = "click_already_claimed";

    /// <summary>No account hash was supplied, so login reconciliation had nothing to work with.</summary>
    public const string NoLoginKey = "no_login_key";

    /// <summary>No click in the window carried that account hash.</summary>
    public const string LoginNotFound = "login_not_found";

    /// <summary>No claim code was supplied.</summary>
    public const string NoClaimCode = "no_claim_code";

    /// <summary>The probabilistic module is off, not in the configured order, or unconsented.</summary>
    public const string ProbabilisticUnavailable = "probabilistic_unavailable";

    /// <summary>The request carried no signal the scorer could use.</summary>
    public const string NoSignals = "no_signals";

    /// <summary>No candidate click inside the window (TC-147).</summary>
    public const string OutsideWindow = "outside_window";

    /// <summary>The best candidate scored below the configured minimum.</summary>
    public const string BelowMinConfidence = "below_min_confidence";

    /// <summary>Every configured strategy was tried and none of them matched.</summary>
    public const string NoStrategyMatched = "no_strategy_matched";

    /// <summary>The URL reported by a <c>link_open</c> event is not one of this tenant's links.</summary>
    public const string UnknownLink = "unknown_link";
}
