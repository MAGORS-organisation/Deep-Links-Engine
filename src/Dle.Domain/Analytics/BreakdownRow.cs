namespace Dle.Domain.Analytics;

/// <summary>
/// One row of a dimensional breakdown report (FR-202), for example clicks per country or per
/// campaign. Rows are ordered by <see cref="Clicks"/> descending and truncated to
/// <see cref="AnalyticsQuery.Limit"/>.
/// </summary>
/// <param name="Key">Value of the grouped dimension. Never <see langword="null"/>: rows whose
/// dimension is unknown are reported under the literal key <c>unknown</c> rather than dropped,
/// so the sum of the rows always matches the totals.</param>
/// <param name="Clicks">Number of clicks in the group.</param>
/// <param name="Installs">Number of installs attributed to the group.</param>
/// <param name="Value">Summed monetary value of conversions in the group, or
/// <see langword="null"/> when the group produced no conversion carrying a value.</param>
public sealed record BreakdownRow(string Key, long Clicks, long Installs, decimal? Value);
