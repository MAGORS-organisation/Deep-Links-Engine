namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the breakdown query, mapped by Dapper.</summary>
internal sealed class BreakdownRawRow
{
    /// <summary>Value of the grouped dimension.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Clicks in the group.</summary>
    public long Clicks { get; set; }

    /// <summary>Installs in the group.</summary>
    public long Installs { get; set; }

    /// <summary>Conversion events in the group; distinguishes a zero value from no value at all.</summary>
    public long Conversions { get; set; }

    /// <summary>Summed monetary value of the conversions in the group.</summary>
    public decimal ConversionValue { get; set; }
}
