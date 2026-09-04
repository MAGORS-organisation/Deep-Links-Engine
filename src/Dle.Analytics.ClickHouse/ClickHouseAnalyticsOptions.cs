using System.ComponentModel.DataAnnotations;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Configuration of the opt-in ClickHouse analytics provider (ADR-006), bound from the
/// <c>Dle:Analytics:ClickHouse</c> section. The connection string itself is taken from
/// <c>ConnectionStrings:ClickHouse</c> (SHARED-KERNEL §16) and copied into
/// <see cref="ConnectionString"/> during registration.
/// </summary>
/// <remarks>
/// None of these values is read unless <c>Dle:Analytics:Provider</c> is <c>clickhouse</c>: the
/// options are only registered when the provider is actually selected, so a default installation
/// pays neither the validation nor the binding cost.
/// </remarks>
public sealed class ClickHouseAnalyticsOptions
{
    /// <summary>Name of the configuration section this type is bound from.</summary>
    public const string SectionName = "Dle:Analytics:ClickHouse";

    /// <summary>
    /// ADO connection string for the ClickHouse HTTP interface, for example
    /// <c>Host=clickhouse;Port=8123;Database=dle;Username=dle;Password=…</c>. Populated from
    /// <c>ConnectionStrings:ClickHouse</c> when that key is present.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Per-statement timeout for reporting queries, in seconds.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Number of rows the event sink sends to the server per insert block.</summary>
    [Range(100, 1_000_000)]
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Number of insert blocks the event sink may send concurrently.</summary>
    [Range(1, 64)]
    public int MaxDegreeOfParallelism { get; set; } = 2;

    /// <summary>
    /// Retention of raw event tables in days, written into the <c>TTL</c> clause of the embedded
    /// DDL. Mirrors <c>Dle:Privacy:Retention:RawDays</c> so that both providers expire raw events
    /// on the same schedule (FR-247, §E.6.3).
    /// </summary>
    [Range(1, 3650)]
    public int RawRetentionDays { get; set; } = 30;
}
