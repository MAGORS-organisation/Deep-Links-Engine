using Dle.Analytics.Postgres;
using Dle.Domain.Analytics;
using Dle.Domain.Contracts;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// The reporting queries (FR-202, FR-203, FR-205, §B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// Every handler here goes through <see cref="IClickAnalyticsStore"/> and therefore works
/// identically on the PostgreSQL rollups a self-hoster already has and on a ClickHouse cluster an
/// installation past roughly fifty million events a month moves to. That is the whole of ADR-006 as
/// far as the API is concerned: the provider is a configuration value and no endpoint knows which
/// one is behind it.
/// </para>
/// <para>
/// Every query is tenant scoped by the binder and bot-excluded by default. Neither is a per-handler
/// decision; see <see cref="AnalyticsQueryBinder"/> for why.
/// </para>
/// </remarks>
public static class GetAnalytics
{
    /// <summary>
    /// Handles the time series report.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="options">Analytics options, for the row bound.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the buckets and the totals, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The points carry clicks, installs and conversions together because that is what the port
    /// returns: the three come out of one pass over the same window, and splitting them into three
    /// requests would triple the work to produce numbers that have to line up anyway.
    /// </remarks>
    public static async Task<IResult> TimeSeriesAsync(
        HttpContext context,
        IClickAnalyticsStore store,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        IReadOnlyList<TimeSeriesPoint> points = await store.GetTimeSeriesAsync(query, cancellationToken);
        FunnelSummary totals = await store.GetFunnelAsync(query, cancellationToken);

        return TypedResults.Json(
            new TimeSeriesResponse
            {
                Grain = GrainName(query.Grain),
                Points = points,
                Totals = totals,
            },
            AnalyticsJsonContext.Default.TimeSeriesResponse);
    }

    /// <summary>
    /// Handles a dimensional breakdown.
    /// </summary>
    /// <param name="dimension">Dimension to group by, from the allowlist.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="options">Analytics options, for the row bound.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the rows, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> BreakdownAsync(
        string? dimension,
        HttpContext context,
        IClickAnalyticsStore store,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        if (!AnalyticsQueryBinder.TryResolveDimension(dimension, out string resolved, out IResult? problem))
        {
            return problem!;
        }

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        IReadOnlyList<BreakdownRow> rows = await store.GetBreakdownAsync(query, resolved, cancellationToken);

        return TypedResults.Json(
            new BreakdownResponse
            {
                Dimension = resolved,
                Rows = rows,
            },
            AnalyticsJsonContext.Default.BreakdownResponse);
    }

    /// <summary>
    /// Handles the funnel report: clicks to installs to attributed installs to conversions.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="options">Analytics options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the summary, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> FunnelAsync(
        HttpContext context,
        IClickAnalyticsStore store,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        FunnelSummary summary = await store.GetFunnelAsync(query, cancellationToken);

        return TypedResults.Json(summary, AnalyticsJsonContext.Default.FunnelSummary);
    }

    /// <summary>
    /// Handles the attribution quality report — the deterministic, probabilistic and unmatched
    /// split with the mean confidence of the guesses (ADR-008, §0.2).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="options">Analytics options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the split, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> AttributionQualityAsync(
        HttpContext context,
        IClickAnalyticsStore store,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        IReadOnlyList<MatchTypeSummary> rows = await store.GetMatchTypesAsync(query, cancellationToken);

        return TypedResults.Json(
            AttributionQuality.From(rows),
            AnalyticsJsonContext.Default.AttributionQualityResponse);
    }

    /// <summary>Wire name of a bucket size.</summary>
    /// <param name="grain">The grain.</param>
    /// <returns>The name, as it appears in the request and in the response.</returns>
    internal static string GrainName(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "hour",
        TimeGrain.Week => "week",
        TimeGrain.Month => "month",
        _ => "day",
    };
}
