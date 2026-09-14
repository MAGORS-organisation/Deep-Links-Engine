namespace Dle.Domain.Analytics;

/// <summary>
/// Distribution of attribution strategies over the reported interval — the data behind the
/// honest dashboard demanded by ADR-008 ("78 % deterministic, 14 % probabilistic with an average
/// confidence of 0.71, 8 % unmatched").
/// </summary>
/// <remarks>
/// This report exists because commercial mobile measurement partners systematically blur the
/// line between a deterministic and a probabilistic match. Reporting the two separately, with
/// the average confidence attached, is a product feature rather than a diagnostic.
/// </remarks>
/// <param name="MatchType">Strategy name, one of the constants on
/// <c>Dle.Domain.Attribution.MatchTypeNames</c>.</param>
/// <param name="Count">Number of attribution records produced by that strategy.</param>
/// <param name="AverageConfidence">Mean confidence of those records, rounded to two decimal
/// places. Exactly <c>1.00</c> for every deterministic strategy.</param>
public sealed record MatchTypeSummary(string MatchType, long Count, decimal AverageConfidence);
