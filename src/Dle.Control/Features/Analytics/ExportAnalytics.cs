using Dle.Analytics.Postgres;
using Dle.Control.Features.Shared;
using Dle.Domain.Analytics;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// <c>GET /api/v1/analytics/export</c> — a report as a downloadable file (FR-203).
/// </summary>
/// <remarks>
/// <para>
/// The formats come from <see cref="IAnalyticsExporter"/>, which is registered once per format, so
/// this handler picks by name rather than switching on a string of its own. Both shipped exporters
/// take an already materialised report rather than a query, and that is the important property: the
/// store decides what a tenant may see, and an exporter must never become a second way to ask.
/// </para>
/// <para>
/// The response is streamed. A two year daily breakdown by link is a large file, and buffering it
/// to produce a <c>Content-Length</c> would trade the memory of the whole report for a header
/// nobody needs.
/// </para>
/// </remarks>
public static class ExportAnalytics
{
    /// <summary>Report name of the time series export.</summary>
    public const string TimeSeriesReport = "timeseries";

    /// <summary>Report name of the dimensional breakdown export.</summary>
    public const string BreakdownReport = "breakdown";

    /// <summary>Report name of the attribution quality export (ADR-008).</summary>
    public const string AttributionQualityReport = "attribution_quality";

    /// <summary>Every report name this endpoint can export.</summary>
    public static IReadOnlyList<string> Reports { get; } =
    [
        TimeSeriesReport,
        BreakdownReport,
        AttributionQualityReport,
    ];

    /// <summary>
    /// Handles an export.
    /// </summary>
    /// <param name="report">Which report, from <see cref="Reports"/>.</param>
    /// <param name="format">Which format: <c>csv</c> or <c>parquet</c>.</param>
    /// <param name="dimension">Dimension to group by, for the breakdown report.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="exporters">Every registered exporter.</param>
    /// <param name="options">Analytics options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the file, or a problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<IResult> HandleAsync(
        string? report,
        string? format,
        string? dimension,
        HttpContext context,
        IClickAnalyticsStore store,
        IEnumerable<IAnalyticsExporter> exporters,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(exporters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        string requested = string.IsNullOrWhiteSpace(report)
            ? TimeSeriesReport
            : report.Trim().ToLowerInvariant().Replace('-', '_');

        if (!Reports.Contains(requested, StringComparer.Ordinal))
        {
            return DleProblemResults.ValidationFailed(
                "The requested report is not one this endpoint can export.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["report"] =
                    [
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Supported reports: {string.Join(", ", Reports)}."),
                    ],
                });
        }

        string normalizedFormat = AnalyticsExportFormats.Normalize(format);
        IAnalyticsExporter? exporter = null;

        foreach (IAnalyticsExporter candidate in exporters)
        {
            if (string.Equals(candidate.Format, normalizedFormat, StringComparison.Ordinal))
            {
                exporter = candidate;
                break;
            }
        }

        if (exporter is null)
        {
            return DleProblemResults.DependencyUnavailable(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No exporter is registered for the '{normalizedFormat}' format."));
        }

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        string resolvedDimension = string.Empty;

        if (string.Equals(requested, BreakdownReport, StringComparison.Ordinal)
            && !AnalyticsQueryBinder.TryResolveDimension(dimension, out resolvedDimension, out IResult? problem))
        {
            return problem!;
        }

        // The report is materialised before the response starts, so a query failure is still a
        // problem document rather than a truncated file with a 200 already on the wire.
        IReadOnlyList<TimeSeriesPoint> points = [];
        IReadOnlyList<BreakdownRow> rows = [];
        IReadOnlyList<MatchTypeSummary> matchTypes = [];

        switch (requested)
        {
            case BreakdownReport:
                rows = await store.GetBreakdownAsync(query, resolvedDimension, cancellationToken);
                break;

            case AttributionQualityReport:
                matchTypes = await store.GetMatchTypesAsync(query, cancellationToken);
                break;

            case TimeSeriesReport:
            default:
                points = await store.GetTimeSeriesAsync(query, cancellationToken);
                break;
        }

        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"dle-{requested.Replace('_', '-')}-{query.From:yyyyMMdd}-{query.To:yyyyMMdd}.{exporter.FileExtension}");

        return TypedResults.Stream(
            stream => WriteAsync(
                exporter,
                stream,
                requested,
                resolvedDimension,
                points,
                rows,
                matchTypes,
                cancellationToken),
            exporter.ContentType,
            fileName);
    }

    /// <summary>Writes the materialised report through the chosen exporter.</summary>
    private static Task WriteAsync(
        IAnalyticsExporter exporter,
        Stream destination,
        string report,
        string dimension,
        IReadOnlyList<TimeSeriesPoint> points,
        IReadOnlyList<BreakdownRow> rows,
        IReadOnlyList<MatchTypeSummary> matchTypes,
        CancellationToken cancellationToken) => report switch
        {
            BreakdownReport => exporter.WriteBreakdownAsync(destination, dimension, rows, cancellationToken),
            AttributionQualityReport => exporter.WriteMatchTypesAsync(destination, matchTypes, cancellationToken),
            _ => exporter.WriteTimeSeriesAsync(destination, points, cancellationToken),
        };
}
