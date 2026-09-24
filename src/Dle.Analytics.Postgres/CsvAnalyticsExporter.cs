using System.Text;

namespace Dle.Analytics.Postgres;

/// <summary>
/// RFC 4180 CSV export of analytics reports (FR-203).
/// </summary>
/// <remarks>
/// UTF-8 without a byte order mark, CRLF line endings as RFC 4180 requires, and every timestamp
/// written as an explicit UTC instant. Numbers are formatted with
/// <see cref="CultureInfo.InvariantCulture"/>, so a decimal separator never changes with the
/// server's locale — a class of bug that only shows up after the export has been trusted for
/// months.
/// </remarks>
public sealed class CsvAnalyticsExporter : IAnalyticsExporter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public string Format => AnalyticsExportFormats.Csv;

    /// <inheritdoc />
    public string ContentType => "text/csv; charset=utf-8";

    /// <inheritdoc />
    public string FileExtension => "csv";

    /// <inheritdoc />
    public async Task WriteTimeSeriesAsync(
        Stream destination,
        IReadOnlyList<TimeSeriesPoint> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        await using StreamWriter writer = CreateWriter(destination);

        await WriteLineAsync(writer, ["bucket", "clicks", "installs", "conversions"], ct);

        foreach (TimeSeriesPoint row in rows)
        {
            await WriteLineAsync(
                writer,
                [
                    Timestamp(row.Bucket),
                    Number(row.Clicks),
                    Number(row.Installs),
                    Number(row.Conversions),
                ],
                ct);
        }

        await writer.FlushAsync(ct);
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

        await using StreamWriter writer = CreateWriter(destination);

        await WriteLineAsync(writer, ["dimension", "key", "clicks", "installs", "value"], ct);

        foreach (BreakdownRow row in rows)
        {
            await WriteLineAsync(
                writer,
                [
                    dimension,
                    row.Key,
                    Number(row.Clicks),
                    Number(row.Installs),
                    row.Value is { } value ? Number(value) : string.Empty,
                ],
                ct);
        }

        await writer.FlushAsync(ct);
    }

    /// <inheritdoc />
    public async Task WriteMatchTypesAsync(
        Stream destination,
        IReadOnlyList<MatchTypeSummary> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        await using StreamWriter writer = CreateWriter(destination);

        await WriteLineAsync(writer, ["match_type", "count", "average_confidence"], ct);

        foreach (MatchTypeSummary row in rows)
        {
            await WriteLineAsync(
                writer,
                [row.MatchType, Number(row.Count), Number(row.AverageConfidence)],
                ct);
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Quotes one field per RFC 4180: only when it contains a comma, a quote, a carriage return or
    /// a line feed, and then with every embedded quote doubled.
    /// </summary>
    /// <param name="value">The raw field value.</param>
    /// <returns>The field as it appears in the file.</returns>
    internal static string Escape(string value)
    {
        bool needsQuotes = value.AsSpan().IndexOfAny(",\"\r\n") >= 0;

        if (!needsQuotes)
        {
            return value;
        }

        return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static StreamWriter CreateWriter(Stream destination) =>
        new(destination, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
        };

    private static async Task WriteLineAsync(
        StreamWriter writer,
        IReadOnlyList<string> fields,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        StringBuilder line = new();

        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            line.Append(Escape(fields[i]));
        }

        await writer.WriteLineAsync(line, ct);
    }
}
