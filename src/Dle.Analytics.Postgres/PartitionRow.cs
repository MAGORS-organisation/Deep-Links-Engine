namespace Dle.Analytics.Postgres;

/// <summary>Row shape of the partition catalogue query, mapped by Dapper.</summary>
internal sealed class PartitionRow
{
    /// <summary>Relation name of the partition, unqualified.</summary>
    public string PartitionName { get; set; } = string.Empty;

    /// <summary>
    /// Exclusive upper bound of the partition range, or <see langword="null"/> when the bound is
    /// not a plain half-open timestamp range. A null bound is never dropped.
    /// </summary>
    public DateTime? UpperBound { get; set; }
}
