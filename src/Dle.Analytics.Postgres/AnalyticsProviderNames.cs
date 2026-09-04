namespace Dle.Analytics.Postgres;

/// <summary>
/// Accepted values of the <c>Dle:Analytics:Provider</c> configuration key (SHARED-KERNEL §16).
/// </summary>
/// <remarks>
/// ADR-006 in one sentence: a self-hoster must not need a second database on day one, and an
/// installation past roughly fifty million events a month must be able to move without a rewrite.
/// The provider name is the only switch between the two.
/// </remarks>
public static class AnalyticsProviderNames
{
    /// <summary>Partitioned PostgreSQL tables with rollups. The default.</summary>
    public const string Postgres = "postgres";

    /// <summary>ClickHouse. Opt-in, for installations that outgrew PostgreSQL.</summary>
    public const string ClickHouse = "clickhouse";

    /// <summary>
    /// Normalises a configured provider name.
    /// </summary>
    /// <param name="value">The raw configuration value. <see langword="null"/>, empty and
    /// whitespace all mean "not configured".</param>
    /// <returns><see cref="ClickHouse"/> when the value names ClickHouse; otherwise
    /// <see cref="Postgres"/>. An unrecognised value falls back to the default rather than
    /// failing, because an analytics provider typo must not stop a deployment that would
    /// otherwise serve links.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Postgres;
        }

        return string.Equals(value.Trim(), ClickHouse, StringComparison.OrdinalIgnoreCase)
            ? ClickHouse
            : Postgres;
    }

    /// <summary>Determines whether a configured value names a provider this build knows.</summary>
    /// <param name="value">The raw configuration value.</param>
    /// <returns><see langword="true"/> when the value is absent or names a known provider.</returns>
    public static bool IsKnown(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), Postgres, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), ClickHouse, StringComparison.OrdinalIgnoreCase);
}
