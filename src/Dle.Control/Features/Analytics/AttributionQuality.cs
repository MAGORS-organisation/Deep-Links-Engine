using Dle.Domain.Analytics;
using Dle.Domain.Attribution;
using Dle.Domain.Contracts;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// Turns the per match type counts into the honest attribution split (ADR-008, §0.2, FR-202).
/// </summary>
/// <remarks>
/// <para>
/// This is the product's argument, and it should be read as one rather than as a report.
/// </para>
/// <para>
/// Commercial measurement partners do not publish this number. They report an attributed install
/// count, and a substantial and unstated share of it is produced by fingerprint matching whose
/// accuracy §A.2.5 puts at roughly a coin flip beyond twenty-four hours. The customer sees one
/// figure, cannot tell which part of it is a fact and which part is a guess, and makes budget
/// decisions on the mixture. That is not a technical shortcoming; it is a commercial incentive,
/// because a larger attributed number sells better than an accurate one.
/// </para>
/// <para>
/// So this engine states the split plainly: how many installs were matched deterministically —
/// confidence exactly 1.00, from an install referrer, a login reconciliation, a typed claim code
/// or a reported direct open — how many were matched probabilistically and at what mean confidence,
/// and how many were not matched at all. The unmatched share is published rather than hidden,
/// because a product that only shows what it caught cannot be checked. ADR-008 asks for a dashboard
/// panel that says "78 % deterministic, 14 % probabilistic at 0.71, 8 % unmatched", and this is the
/// endpoint behind it.
/// </para>
/// <para>
/// A deployment running the default configuration will see zero probabilistic matches, because the
/// probabilistic strategy is off by default and has to be both enabled and named in the strategy
/// order before it runs. That is also intentional: the number is meant to be small, and visible
/// when it is not.
/// </para>
/// </remarks>
public static class AttributionQuality
{
    /// <summary>
    /// Builds the response from the store's per match type counts.
    /// </summary>
    /// <param name="rows">Counts by match type, including <c>none</c> for unmatched installs.</param>
    /// <returns>The split.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The mean confidence is weighted by count and covers the probabilistic matches only. An
    /// average over every match type would be dominated by the deterministic ones, always sit just
    /// under 1.00, and say nothing at all — which is exactly the kind of comforting number this
    /// endpoint exists to refuse.
    /// </remarks>
    public static AttributionQualityResponse From(IReadOnlyList<MatchTypeSummary> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        long deterministic = 0;
        long probabilistic = 0;
        long unmatched = 0;
        decimal confidenceSum = 0m;

        foreach (MatchTypeSummary row in rows)
        {
            switch (MatchTypeNames.Parse(row.MatchType))
            {
                case MatchType.InstallReferrer:
                case MatchType.Login:
                case MatchType.ClaimCode:
                case MatchType.DirectOpen:
                    deterministic += row.Count;
                    break;

                case MatchType.Probabilistic:
                    probabilistic += row.Count;
                    confidenceSum += row.AverageConfidence * row.Count;
                    break;

                case MatchType.None:
                default:
                    // Anything this build does not recognise counts as unmatched rather than as a
                    // match. A future match type must not silently inflate the deterministic share
                    // of an older reader (SHARED-KERNEL §17.9).
                    unmatched += row.Count;
                    break;
            }
        }

        decimal meanConfidence = probabilistic > 0
            ? decimal.Round(confidenceSum / probabilistic, 2, MidpointRounding.AwayFromZero)
            : 0m;

        return new AttributionQualityResponse
        {
            Deterministic = deterministic,
            Probabilistic = probabilistic,
            Unmatched = unmatched,
            AverageProbabilisticConfidence = meanConfidence,
            ByMatchType = rows,
        };
    }
}
