using System.Collections.ObjectModel;

namespace Dle.Domain.Attribution;

/// <summary>
/// Outcome of an attribution attempt. Carries the strategy and the confidence side by side, so a
/// probabilistic match can never be presented as a certainty (FR-186, ADR-008).
/// </summary>
/// <remarks>
/// <see cref="Evidence"/> is not decoration. Every attribution has to be explainable after the
/// fact: when a customer asks why an install was credited to a campaign, the recorded evidence is
/// the only defence (§B.5.3). For a probabilistic match it lists the signals that agreed and the
/// weight each contributed.
/// </remarks>
public sealed record AttributionResult
{
    /// <summary>Whether any strategy matched. <see langword="false"/> means the install is
    /// reported as organic and the application continues normally (TC-142).</summary>
    public required bool Matched { get; init; }

    /// <summary>The strategy that produced the match.</summary>
    public required MatchType MatchType { get; init; }

    /// <summary>Confidence in the range 0.00 to 1.00, with two decimal places. Exactly
    /// <c>1.00</c> for every deterministic strategy and strictly below it otherwise.</summary>
    public required decimal Confidence { get; init; }

    /// <summary>Click identifier the install was matched to.</summary>
    public string? ClickId { get; init; }

    /// <summary>Link the matched click belongs to.</summary>
    public long? LinkId { get; init; }

    /// <summary>Deep link path the application should open, for example <c>/product/123</c>.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Campaign name carried by the matched link.</summary>
    public string? Campaign { get; init; }

    /// <summary>Parameters handed to the application, for example the UTM set of the link.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>What exactly decided the match, for audit purposes. Stored in
    /// <c>attributions.evidence</c>.</summary>
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Creates a negative result that records why nothing matched.</summary>
    /// <param name="reason">Short machine readable reason, for example <c>no_click_id</c>,
    /// <c>consent_missing</c>, <c>outside_window</c> or <c>click_already_claimed</c>. It is kept
    /// in <see cref="Evidence"/> under the key <c>reason</c>, because "no match" is an answer that
    /// has to be explainable too.</param>
    /// <returns>An unmatched result with <see cref="MatchType.None"/> and zero confidence.</returns>
    public static AttributionResult NoMatch(string reason) => new()
    {
        Matched = false,
        MatchType = Attribution.MatchType.None,
        Confidence = 0m,
        Evidence = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason ?? string.Empty }),
    };
}
