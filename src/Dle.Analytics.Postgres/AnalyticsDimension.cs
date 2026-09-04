namespace Dle.Analytics.Postgres;

/// <summary>
/// One breakdown dimension resolved to the SQL that groups by it.
/// </summary>
/// <remarks>
/// Both expressions are composite format strings with a single placeholder for the table alias, so
/// the same dimension can be applied to <c>click_events</c>, to a rollup table, or to the click
/// side of a join without being written three times.
/// </remarks>
/// <param name="Name">Canonical, lowercase dimension name.</param>
/// <param name="RawExpression">Grouping expression over the raw <c>click_events</c> table.</param>
/// <param name="RollupExpression">Grouping expression over the click rollup tables, or
/// <see langword="null"/> when the rollups do not carry this dimension and the report must be
/// computed from raw events.</param>
/// <param name="NeedsLinkJoin">Whether the raw expression requires the <c>links</c> table to be
/// joined as <c>l</c>.</param>
public sealed record AnalyticsDimension(
    string Name,
    string RawExpression,
    string? RollupExpression,
    bool NeedsLinkJoin);
