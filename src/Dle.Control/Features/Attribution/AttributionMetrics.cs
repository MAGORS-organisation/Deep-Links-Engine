using System.Diagnostics.Metrics;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The instruments the attribution module publishes (§C.6).
/// </summary>
/// <remarks>
/// <para>
/// The two instruments §C.6 names are <c>dle_attribution_total</c>, tagged by match type, and
/// <c>dle_attribution_confidence</c> as a histogram. Together they are the honest dashboard ADR-008
/// asks for: "78 % of attributions deterministic, 14 % probabilistic at average confidence 0.71,
/// 8 % unmatched". A counter without the confidence distribution would let a deployment quietly
/// drift towards guessing and still look healthy.
/// </para>
/// <para>
/// Instrument names use the OpenTelemetry dotted spelling; a Prometheus exporter renders
/// <c>dle.attribution</c> as <c>dle_attribution_total</c> and <c>dle.attribution.confidence</c> as
/// <c>dle_attribution_confidence</c>.
/// </para>
/// </remarks>
public sealed class AttributionMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics provider.</summary>
    public const string MeterName = "Dle.Control.Attribution";

    private const string MatchTypeTag = "match_type";
    private const string StrategyTag = "strategy";
    private const string OutcomeTag = "outcome";

    private readonly Meter _meter;
    private readonly Counter<long> _attributions;
    private readonly Histogram<double> _confidence;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _claimCodes;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">
    /// Meter factory supplied by the host, so a test can observe the instruments through
    /// <c>MetricCollector</c>. When it is <see langword="null"/> the meter is created directly, so
    /// metrics still flow to any listener.
    /// </param>
    public AttributionMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        _attributions = _meter.CreateCounter<long>(
            "dle.attribution",
            unit: "{attribution}",
            description: "Attribution decisions, tagged by match type. 'none' is a decision too (§C.6).");

        _confidence = _meter.CreateHistogram<double>(
            "dle.attribution.confidence",
            unit: "1",
            description: "Distribution of attribution confidence, tagged by match type (§C.6).");

        _rejected = _meter.CreateCounter<long>(
            "dle.attribution.rejected",
            unit: "{attempt}",
            description: "Attribution attempts refused before a decision: rate limited, unauthenticated, or a tampered click identifier.");

        _claimCodes = _meter.CreateCounter<long>(
            "dle.attribution.claim_code",
            unit: "{code}",
            description: "Claim codes by outcome: issued, redeemed, expired, unknown or already consumed (TC-148).");
    }

    /// <summary>Records one attribution decision.</summary>
    /// <param name="matchType">The stored match type name, including <c>none</c>.</param>
    /// <param name="confidence">Confidence of the decision, from 0.00 to 1.00.</param>
    public void Decision(string matchType, decimal confidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchType);

        var tag = new KeyValuePair<string, object?>(MatchTypeTag, matchType);

        _attributions.Add(1, tag);
        _confidence.Record((double)confidence, tag);
    }

    /// <summary>Counts one attempt refused before any decision was reached.</summary>
    /// <param name="outcome">Why it was refused, for example <c>rate_limited</c>.</param>
    public void Rejected(string outcome) =>
        _rejected.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <summary>Counts one tampered click identifier (TC-167).</summary>
    /// <param name="strategy">Strategy that detected it.</param>
    public void Tampered(string strategy) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>(OutcomeTag, "tampered"),
            new KeyValuePair<string, object?>(StrategyTag, strategy));

    /// <summary>Counts one claim code outcome.</summary>
    /// <param name="outcome">One of <c>issued</c>, <c>redeemed</c>, <c>expired</c>,
    /// <c>consumed</c> or <c>unknown</c>.</param>
    public void ClaimCode(string outcome) =>
        _claimCodes.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
