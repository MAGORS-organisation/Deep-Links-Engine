namespace Dle.Domain.Analytics;

/// <summary>
/// Aggregated click to conversion funnel for one <see cref="AnalyticsQuery"/> (FR-202).
/// </summary>
/// <remarks>
/// <see cref="Attributed"/> is reported separately from <see cref="Installs"/> on purpose: the
/// difference between the two is the share of installs the engine could not tie back to a click,
/// and hiding it would be exactly the kind of laundering the product refuses to do (ADR-008).
/// </remarks>
/// <param name="Clicks">Human clicks in the reported interval.</param>
/// <param name="Installs">First application opens in the reported interval.</param>
/// <param name="Attributed">Installs that were matched to a click by any strategy. Always less
/// than or equal to <paramref name="Installs"/>.</param>
/// <param name="Conversions">Conversion events reported by the SDK in the interval.</param>
/// <param name="ConversionRate">Conversions divided by clicks, rounded to four decimal places.
/// Zero when there were no clicks; the value is never extrapolated.</param>
public sealed record FunnelSummary(
    long Clicks,
    long Installs,
    long Attributed,
    long Conversions,
    decimal ConversionRate);
