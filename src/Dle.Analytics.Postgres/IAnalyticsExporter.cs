namespace Dle.Analytics.Postgres;

/// <summary>
/// Writes an analytics report to a stream in one export format (FR-203).
/// </summary>
/// <remarks>
/// Both implementations are registered, so the reporting endpoint resolves
/// <c>IEnumerable&lt;IAnalyticsExporter&gt;</c> and picks by <see cref="Format"/> rather than
/// switching on a string itself. Exporters take an already materialised report rather than a query:
/// the store decides what a tenant may see, and an exporter must never be a second way to ask.
/// </remarks>
public interface IAnalyticsExporter
{
    /// <summary>Format name, one of the constants on <see cref="AnalyticsExportFormats"/>.</summary>
    string Format { get; }

    /// <summary>MIME type to serve the produced bytes with.</summary>
    string ContentType { get; }

    /// <summary>File extension, without the leading dot.</summary>
    string FileExtension { get; }

    /// <summary>Writes a time series report.</summary>
    /// <param name="destination">Stream to write to. Left open.</param>
    /// <param name="rows">The buckets, in the order the store returned them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    Task WriteTimeSeriesAsync(
        Stream destination,
        IReadOnlyList<TimeSeriesPoint> rows,
        CancellationToken ct);

    /// <summary>Writes a dimensional breakdown report.</summary>
    /// <param name="destination">Stream to write to. Left open.</param>
    /// <param name="dimension">Name of the grouped dimension, carried into the output so the file
    /// is self describing.</param>
    /// <param name="rows">The rows, in the order the store returned them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    Task WriteBreakdownAsync(
        Stream destination,
        string dimension,
        IReadOnlyList<BreakdownRow> rows,
        CancellationToken ct);

    /// <summary>Writes the attribution quality report behind the ADR-008 dashboard panel.</summary>
    /// <param name="destination">Stream to write to. Left open.</param>
    /// <param name="rows">The rows, in the order the store returned them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    Task WriteMatchTypesAsync(
        Stream destination,
        IReadOnlyList<MatchTypeSummary> rows,
        CancellationToken ct);
}
