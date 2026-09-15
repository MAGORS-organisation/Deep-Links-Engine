using Parquet.Schema;
using Parquet.Serialization;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Apache Parquet export of analytics reports (FR-203).
/// </summary>
/// <remarks>
/// <para>
/// Column names, order and meaning are identical to <see cref="CsvAnalyticsExporter"/>, so the two
/// formats are the same report in two encodings rather than two subtly different reports. Parquet
/// exists here for the case CSV handles badly: a year of hourly buckets loaded into a warehouse or
/// a notebook, where typed columns and column pruning are worth more than being readable in a text
/// editor.
/// </para>
/// <para>
/// Written through the untyped serializer rather than a mapped class, because the schema is the
/// contract: the column names are chosen here, next to the CSV headers they must match, instead of
/// falling out of C# property naming.
/// </para>
/// </remarks>
public sealed class ParquetAnalyticsExporter : IAnalyticsExporter
{
    /// <inheritdoc />
    public string Format => AnalyticsExportFormats.Parquet;

    /// <inheritdoc />
    public string ContentType => "application/vnd.apache.parquet";

    /// <inheritdoc />
    public string FileExtension => "parquet";

    /// <inheritdoc />
    public async Task WriteTimeSeriesAsync(
        Stream destination,
        IReadOnlyList<TimeSeriesPoint> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        ParquetSchema schema = new(
            new DataField<DateTime>("bucket"),
            new DataField<long>("clicks"),
            new DataField<long>("installs"),
            new DataField<long>("conversions"));

        List<IDictionary<string, object?>> data = new(rows.Count);

        foreach (TimeSeriesPoint row in rows)
        {
            data.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bucket"] = row.Bucket.UtcDateTime,
                ["clicks"] = row.Clicks,
                ["installs"] = row.Installs,
                ["conversions"] = row.Conversions,
            });
        }

        await WriteBufferedAsync(destination, data, schema, ct);
    }

    /// <inheritdoc />
    public async Task WriteBreakdownAsync(
        Stream destination,
        string dimension,
        IReadOnlyList<BreakdownRow> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentNullException.ThrowIfNull(rows);

        ParquetSchema schema = new(
            new DataField<string>("dimension"),
            new DataField<string>("key"),
            new DataField<long>("clicks"),
            new DataField<long>("installs"),
            new DataField<decimal?>("value"));

        List<IDictionary<string, object?>> data = new(rows.Count);

        foreach (BreakdownRow row in rows)
        {
            data.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dimension"] = dimension,
                ["key"] = row.Key,
                ["clicks"] = row.Clicks,
                ["installs"] = row.Installs,

                // A group that produced no conversion carrying a value is written as null, not as
                // zero: BreakdownRow draws that distinction and the export must not erase it.
                ["value"] = row.Value,
            });
        }

        await WriteBufferedAsync(destination, data, schema, ct);
    }

    /// <inheritdoc />
    public async Task WriteMatchTypesAsync(
        Stream destination,
        IReadOnlyList<MatchTypeSummary> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        ParquetSchema schema = new(
            new DataField<string>("match_type"),
            new DataField<long>("count"),
            new DataField<decimal>("average_confidence"));

        List<IDictionary<string, object?>> data = new(rows.Count);

        foreach (MatchTypeSummary row in rows)
        {
            data.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["match_type"] = row.MatchType,
                ["count"] = row.Count,
                ["average_confidence"] = row.AverageConfidence,
            });
        }

        await WriteBufferedAsync(destination, data, schema, ct);
    }

    /// <summary>
    /// Assembles the Parquet file in memory and copies it to the destination asynchronously.
    /// </summary>
    /// <remarks>
    /// Parquet.Net flushes its column pages with synchronous writes, which Kestrel refuses on a
    /// response body (<c>AllowSynchronousIO</c> is off): written straight to the response, the
    /// export died mid-stream with a 500 the client saw as a truncated file. An export is bounded
    /// (at most <see cref="AnalyticsOptions.MaxBreakdownRows"/> rows, or one point per bucket of a
    /// window of at most two years), so the whole file fits comfortably in memory.
    /// </remarks>
    private static async Task WriteBufferedAsync(
        Stream destination,
        IReadOnlyList<IDictionary<string, object?>> data,
        ParquetSchema schema,
        CancellationToken ct)
    {
        using MemoryStream buffer = new();

        await ParquetSerializer.SerializeUntypedAsync(data, schema, buffer, cancellationToken: ct);

        buffer.Position = 0;
        await buffer.CopyToAsync(destination, ct);
        await destination.FlushAsync(ct);
    }
}
