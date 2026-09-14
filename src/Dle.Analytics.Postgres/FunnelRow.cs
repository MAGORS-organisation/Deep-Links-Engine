namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the funnel query, mapped by Dapper.</summary>
internal sealed class FunnelRow
{
    /// <summary>Human clicks in the interval.</summary>
    public long Clicks { get; set; }

    /// <summary>First application opens in the interval.</summary>
    public long Installs { get; set; }

    /// <summary>Installs matched to a click by any strategy.</summary>
    public long Attributed { get; set; }

    /// <summary>Conversion events reported by the SDK.</summary>
    public long Conversions { get; set; }
}
