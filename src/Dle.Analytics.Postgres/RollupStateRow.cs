namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the rollup watermark query, mapped by Dapper.</summary>
internal sealed class RollupStateRow
{
    /// <summary>Name of the rollup table.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Instant up to which the rollup is complete, exclusive.</summary>
    public DateTime CoveredThrough { get; set; }

    /// <summary>
    /// The earliest instant this rollup has aggregated, or <see langword="null"/> when the row
    /// predates the column and the reach of the rollup is therefore unknown.
    /// </summary>
    public DateTime? CoveredFrom { get; set; }
}
