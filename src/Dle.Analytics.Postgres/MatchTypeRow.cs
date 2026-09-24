namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the attribution-quality query, mapped by Dapper.</summary>
internal sealed class MatchTypeRow
{
    /// <summary>Strategy name, one of the constants on <c>MatchTypeNames</c>.</summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>Number of attributions produced by that strategy.</summary>
    public long Count { get; set; }

    /// <summary>Mean confidence of those attributions.</summary>
    public decimal AverageConfidence { get; set; }
}
