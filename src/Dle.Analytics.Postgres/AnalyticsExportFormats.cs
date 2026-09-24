namespace Dle.Analytics.Postgres;

/// <summary>
/// Export formats offered by <see cref="IAnalyticsExporter"/> (FR-203).
/// </summary>
public static class AnalyticsExportFormats
{
    /// <summary>RFC 4180 comma separated values, UTF-8.</summary>
    public const string Csv = "csv";

    /// <summary>Apache Parquet, Snappy compressed by the writer's defaults.</summary>
    public const string Parquet = "parquet";

    /// <summary>Every supported format name.</summary>
    public static IReadOnlyList<string> All { get; } = [Csv, Parquet];

    /// <summary>Normalises a requested format name.</summary>
    /// <param name="value">The requested format. Case and whitespace are ignored.</param>
    /// <returns><see cref="Parquet"/> when the value names Parquet; otherwise <see cref="Csv"/>,
    /// which is the format every consumer can read.</returns>
    public static string Normalize(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), Parquet, StringComparison.OrdinalIgnoreCase)
            ? Parquet
            : Csv;
}
