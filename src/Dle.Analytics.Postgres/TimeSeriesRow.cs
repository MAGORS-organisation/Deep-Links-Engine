namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the time-series query, mapped by Dapper.</summary>
internal sealed class TimeSeriesRow
{
    /// <summary>Bucket start, read as a UTC <see cref="DateTime"/> from a <c>timestamptz</c>.</summary>
    public DateTime Bucket { get; set; }

    /// <summary>Clicks in the bucket.</summary>
    public long Clicks { get; set; }

    /// <summary>Installs in the bucket.</summary>
    public long Installs { get; set; }

    /// <summary>Conversions in the bucket.</summary>
    public long Conversions { get; set; }
}
