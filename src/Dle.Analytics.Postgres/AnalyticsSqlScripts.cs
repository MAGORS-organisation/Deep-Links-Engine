using System.Collections.Concurrent;
using System.Reflection;

namespace Dle.Analytics.Postgres;

/// <summary>
/// The SQL this module ships, embedded in the assembly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Migration"/> is the schema: the rollup tables, the shared platform function, the
/// rollup watermark and the retention audit table. It is idempotent and is meant to be applied by
/// the persistence migration, not by this module — an application does not create its own schema
/// at startup.
/// </para>
/// <para>
/// The remaining scripts are the parameterised statements the rollup and retention services run.
/// They live in the same folder as the schema on purpose: a rollup whose maintenance SQL is buried
/// in string constants is a rollup nobody will review.
/// </para>
/// </remarks>
public static class AnalyticsSqlScripts
{
    /// <summary>Schema for the rollups, the watermark and the retention audit trail.</summary>
    public const string Migration = "001_analytics_rollups.sql";

    /// <summary>Recomputes hourly click buckets from <c>click_events</c>.</summary>
    public const string RefreshClickRollupHourly = "002_refresh_click_rollup_hourly.sql";

    /// <summary>Rolls hourly click buckets up into daily ones.</summary>
    public const string RefreshClickRollupDaily = "003_refresh_click_rollup_daily.sql";

    /// <summary>Recomputes hourly install and conversion buckets.</summary>
    public const string RefreshInstallRollupHourly = "004_refresh_install_rollup_hourly.sql";

    /// <summary>Rolls hourly install buckets up into daily ones.</summary>
    public const string RefreshInstallRollupDaily = "005_refresh_install_rollup_daily.sql";

    /// <summary>Recomputes the daily attribution-quality buckets behind the ADR-008 panel.</summary>
    public const string RefreshAttributionQualityDaily =
        "006_refresh_attribution_quality_daily.sql";

    /// <summary>Advances a rollup watermark.</summary>
    public const string UpsertRollupState = "007_upsert_rollup_state.sql";

    /// <summary>Lists the range partitions of a partitioned table with their upper bounds.</summary>
    public const string ListPartitions = "008_list_partitions.sql";

    /// <summary>Clears <c>ip_prefix</c> from raw events older than a cutoff.</summary>
    public const string AnonymiseIpPrefix = "009_anonymise_ip_prefix.sql";

    /// <summary>Deletes rollup rows older than a cutoff.</summary>
    public const string PruneRollups = "010_prune_rollups.sql";

    /// <summary>Records one execution of the retention job.</summary>
    public const string RecordRetentionRun = "011_record_retention_run.sql";

    private const string ResourcePrefix = "Dle.Analytics.Postgres.Sql.";

    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The schema scripts a migration should apply, in order.
    /// </summary>
    /// <remarks>
    /// A list rather than "everything in the folder": the parameterised maintenance statements
    /// live in the same folder and must never be handed to a migration runner.
    /// </remarks>
    public static IReadOnlyList<string> MigrationScripts { get; } = [Migration];

    /// <summary>Reads one embedded script by file name.</summary>
    /// <param name="name">File name, for example <see cref="Migration"/>.</param>
    /// <returns>The script text. Reads are cached, so a hot loop does not touch the manifest.</returns>
    /// <exception cref="InvalidOperationException">The script is not embedded in this assembly,
    /// which means the build lost its <c>Sql</c> folder.</exception>
    public static string Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Cache.GetOrAdd(name, static key =>
        {
            Assembly assembly = typeof(AnalyticsSqlScripts).Assembly;
            string resource = ResourcePrefix + key;

            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Embedded analytics script '{resource}' is missing from {assembly.GetName().Name}.");

            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        });
    }
}
