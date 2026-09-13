using System.Diagnostics.CodeAnalysis;

using Dapper;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Enforces the analytics retention policy against PostgreSQL (FR-247, §E.6.3).
/// </summary>
/// <remarks>
/// <para>
/// The pass does three things, in this order. It detaches and drops every partition of the raw
/// event tables whose entire range is older than the raw window. It optionally clears
/// <c>ip_prefix</c> from the events still inside that window, leaving them hash-only. And it
/// deletes rollup rows older than the aggregate window. Then it writes what it did to
/// <c>analytics_retention_runs</c> — including when the run failed, because a retention job whose
/// failures leave no trace is not a control anybody can rely on.
/// </para>
/// <para>
/// Partitions are removed one statement at a time rather than in one transaction: <c>DETACH</c>
/// takes an ACCESS EXCLUSIVE lock on the parent, and holding one across thirty partitions would
/// stall the click stream writers for as long as the whole pass takes. An interrupted pass simply
/// removes fewer partitions and the next one finishes the job.
/// </para>
/// <para>
/// A dry run reports the partitions it would drop and removes nothing at all — it does not count
/// the rows it would anonymise or delete, because obtaining those counts means the same full scans
/// the real pass performs, and a dry run should be cheap enough to leave on.
/// </para>
/// </remarks>
public sealed partial class PostgresRetentionService : IRetentionService
{
    /// <summary>
    /// Partitioned raw event tables this policy governs. A table that is not partitioned simply
    /// returns no partitions and is skipped.
    /// </summary>
    private static readonly string[] PartitionedTables = ["click_events", "sdk_events"];

    private readonly IAnalyticsConnectionFactory _connections;
    private readonly IOptionsMonitor<AnalyticsRetentionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresRetentionService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="connections">Factory for analytics database connections.</param>
    /// <param name="options">Monitor over the retention options.</param>
    /// <param name="timeProvider">Clock. Never <see cref="DateTime.UtcNow"/> (SHARED-KERNEL §17).</param>
    /// <param name="logger">Logger for the job's own audit trail.</param>
    public PostgresRetentionService(
        IAnalyticsConnectionFactory connections,
        IOptionsMonitor<AnalyticsRetentionOptions> options,
        TimeProvider timeProvider,
        ILogger<PostgresRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "§E.6.3 requires the retention job's own execution to be auditable, which includes the "
            + "runs that fail: the audit row has to be written whatever went wrong. The exception "
            + "is logged and rethrown, so nothing is swallowed and the caller still fails.")]
    public async Task<RetentionRunResult> RunAsync(CancellationToken ct)
    {
        AnalyticsRetentionOptions options = _options.CurrentValue;
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        Guid runId = Guid.CreateVersion7(startedAt);
        List<string> dropped = [];
        long anonymised = 0;
        long rollupRowsDeleted = 0;

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        try
        {
            DateTimeOffset rawCutoff = TruncateDay(startedAt.AddDays(-options.RawDays));

            foreach (string parent in PartitionedTables)
            {
                await DropExpiredPartitionsAsync(
                    connection, parent, rawCutoff, options, dropped, ct);
            }

            if (!options.DryRun)
            {
                await EnsurePartitionsAsync(connection, ct);
            }

            if (options.IpPrefixDays is { } prefixDays && !options.DryRun)
            {
                anonymised = await ExecuteWithCutoffAsync(
                    connection,
                    AnalyticsSqlScripts.AnonymiseIpPrefix,
                    startedAt.AddDays(-prefixDays),
                    ct);
            }

            if (!options.DryRun)
            {
                rollupRowsDeleted = await ExecuteWithCutoffAsync(
                    connection,
                    AnalyticsSqlScripts.PruneRollups,
                    TruncateDay(startedAt.AddDays(-options.AggregatedDays)),
                    ct);
            }

            RetentionRunResult result = new()
            {
                Id = runId,
                StartedAt = startedAt,
                FinishedAt = _timeProvider.GetUtcNow(),
                RawDays = options.RawDays,
                AggregatedDays = options.AggregatedDays,
                IpPrefixDays = options.IpPrefixDays,
                DryRun = options.DryRun,
                PartitionsDropped = dropped,
                RowsAnonymised = anonymised,
                RollupRowsDeleted = rollupRowsDeleted,
                Status = options.DryRun
                    ? RetentionRunResult.StatusDryRun
                    : RetentionRunResult.StatusOk,
            };

            await RecordAsync(connection, result, ct);

            LogRetentionCompleted(
                result.Status,
                rawCutoff,
                dropped.Count,
                dropped.Count == 0 ? "(none)" : string.Join(", ", dropped),
                anonymised,
                rollupRowsDeleted);

            return result;
        }
        catch (Exception ex)
        {
            RetentionRunResult failure = new()
            {
                Id = runId,
                StartedAt = startedAt,
                FinishedAt = _timeProvider.GetUtcNow(),
                RawDays = options.RawDays,
                AggregatedDays = options.AggregatedDays,
                IpPrefixDays = options.IpPrefixDays,
                DryRun = options.DryRun,
                PartitionsDropped = dropped,
                RowsAnonymised = anonymised,
                RollupRowsDeleted = rollupRowsDeleted,
                Status = RetentionRunResult.StatusFailed,
                Error = ex.Message,
            };

            LogRetentionFailed(ex, dropped.Count);
            await TryRecordFailureAsync(connection, failure, ct);

            throw;
        }
    }

    /// <summary>
    /// Whether a relation name coming out of the system catalogue is safe to place in a DDL
    /// statement.
    /// </summary>
    /// <param name="identifier">The candidate name.</param>
    /// <returns><see langword="true"/> for a non-empty name of at most 63 characters made only of
    /// ASCII letters, digits and underscores.</returns>
    /// <remarks>
    /// The names already come from <c>pg_class</c> rather than from a caller, so this is the second
    /// lock on the door rather than the first. It exists because <c>ALTER TABLE … DETACH
    /// PARTITION</c> cannot take the partition name as a bound parameter, and a rule that says
    /// "this string is trusted because of where it came from" is exactly the rule that stops being
    /// true after a refactor.
    /// </remarks>
    internal static bool IsSafeIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 63)
        {
            return false;
        }

        foreach (char c in identifier)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset TruncateDay(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTimeOffset(
            new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc));
    }

    private async Task DropExpiredPartitionsAsync(
        NpgsqlConnection connection,
        string parent,
        DateTimeOffset cutoff,
        AnalyticsRetentionOptions options,
        List<string> dropped,
        CancellationToken ct)
    {
        IEnumerable<PartitionRow> partitions = await connection.QueryAsync<PartitionRow>(
            new CommandDefinition(
                AnalyticsSqlScripts.Read(AnalyticsSqlScripts.ListPartitions),
                new { parentTable = parent },
                commandTimeout: _connections.CommandTimeoutSeconds,
                cancellationToken: ct));

        foreach (PartitionRow partition in partitions)
        {
            if (dropped.Count >= options.MaxPartitionsPerRun)
            {
                return;
            }

            if (partition.UpperBound is not { } upper)
            {
                continue;
            }

            DateTimeOffset upperUtc = new(DateTime.SpecifyKind(upper, DateTimeKind.Utc));

            if (upperUtc > cutoff)
            {
                continue;
            }

            if (!IsSafeIdentifier(partition.PartitionName))
            {
                LogPartitionSkipped(partition.PartitionName);
                continue;
            }

            if (!options.DryRun)
            {
                await DetachAndDropAsync(connection, parent, partition.PartitionName, ct);
            }

            dropped.Add(partition.PartitionName);
            LogPartitionRemoved(partition.PartitionName, parent, upperUtc, options.DryRun);
        }
    }

    private async Task DetachAndDropAsync(
        NpgsqlConnection connection,
        string parent,
        string partition,
        CancellationToken ct)
    {
        string detach = $"ALTER TABLE \"{parent}\" DETACH PARTITION \"{partition}\"";
        string drop = $"DROP TABLE IF EXISTS \"{partition}\"";

        await connection.ExecuteAsync(new CommandDefinition(
            detach, commandTimeout: _connections.CommandTimeoutSeconds, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            drop, commandTimeout: _connections.CommandTimeoutSeconds, cancellationToken: ct));
    }

    private async Task<long> ExecuteWithCutoffAsync(
        NpgsqlConnection connection,
        string scriptName,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            AnalyticsSqlScripts.Read(scriptName),
            new { cutoff = cutoff.UtcDateTime },
            commandTimeout: _connections.CommandTimeoutSeconds,
            cancellationToken: ct));

        return affected;
    }

    private async Task RecordAsync(
        NpgsqlConnection connection,
        RetentionRunResult result,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            AnalyticsSqlScripts.Read(AnalyticsSqlScripts.RecordRetentionRun),
            new
            {
                id = result.Id,
                startedAt = result.StartedAt.UtcDateTime,
                finishedAt = result.FinishedAt.UtcDateTime,
                rawDays = result.RawDays,
                aggregatedDays = result.AggregatedDays,
                ipPrefixDays = result.IpPrefixDays,
                dryRun = result.DryRun,
                partitionsDropped = result.PartitionsDropped.ToArray(),
                rowsAnonymised = result.RowsAnonymised,
                rollupRowsDeleted = result.RollupRowsDeleted,
                status = result.Status,
                error = result.Error,
            },
            commandTimeout: _connections.CommandTimeoutSeconds,
            cancellationToken: ct));
    }

    /// <summary>
    /// Writes the audit row for a failed run. A failure to record the failure must not replace the
    /// original exception, which is the one that explains what actually went wrong.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "This runs inside the failure path of RunAsync. If auditing the failure also fails "
            + "there is nothing left to do but log it; letting it propagate would replace the "
            + "original exception with a less informative one.")]
    private async Task TryRecordFailureAsync(
        NpgsqlConnection connection,
        RetentionRunResult failure,
        CancellationToken ct)
    {
        try
        {
            await RecordAsync(connection, failure, ct);
        }
        catch (Exception ex)
        {
            LogRetentionAuditFailed(ex);
        }
    }

    [LoggerMessage(
        EventId = 6300,
        Level = LogLevel.Information,
        Message = "Retention {Status}: cutoff {RawCutoff:o}, {PartitionCount} partitions removed "
                  + "[{Partitions}], {RowsAnonymised} events reduced to hash only, "
                  + "{RollupRowsDeleted} aggregate rows deleted.")]
    private partial void LogRetentionCompleted(
        string status,
        DateTimeOffset rawCutoff,
        int partitionCount,
        string partitions,
        long rowsAnonymised,
        long rollupRowsDeleted);

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Information,
        Message = "Retention removed partition {Partition} of {ParentTable} "
                  + "(upper bound {UpperBound:o}, dry run {DryRun}).")]
    private partial void LogPartitionRemoved(
        string partition,
        string parentTable,
        DateTimeOffset upperBound,
        bool dryRun);

    [LoggerMessage(
        EventId = 6302,
        Level = LogLevel.Warning,
        Message = "Retention refused to touch partition {Partition}: the relation name is not a "
                  + "plain identifier.")]
    private partial void LogPartitionSkipped(string partition);

    [LoggerMessage(
        EventId = 6303,
        Level = LogLevel.Error,
        Message = "Retention failed after removing {PartitionCount} partitions.")]
    private partial void LogRetentionFailed(Exception exception, int partitionCount);

    [LoggerMessage(
        EventId = 6304,
        Level = LogLevel.Error,
        Message = "Retention could not write its own audit row.")]
    private partial void LogRetentionAuditFailed(Exception exception);

    /// <summary>
    /// Pre-creates the coming days' partitions of both event streams and adopts any day whose
    /// rows landed in a default partition meanwhile, through the functions the migration
    /// installs. This is the product-side caller those functions were waiting for: without one,
    /// a deployment without pg_partman ran out of partitions <see cref="PartitionDaysAhead"/>
    /// days after its migration and every event after that went to the default partition, where
    /// retention never reaches it.
    /// </summary>
    /// <remarks>
    /// The retention argument is <see langword="null"/> on purpose: this service drops expired
    /// partitions itself, one at a time and with an audit row each, and the function must not
    /// drop anything behind its back.
    /// </remarks>
    private async Task EnsurePartitionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        int created = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT dle_click_events_maintain(@daysAhead, NULL) + dle_sdk_events_maintain(@daysAhead, NULL)",
                new { daysAhead = PartitionDaysAhead },
                commandTimeout: _connections.CommandTimeoutSeconds,
                cancellationToken: ct));

        if (created > 0)
        {
            LogPartitionsCreated(created, PartitionDaysAhead);
        }
    }

    /// <summary>How many days ahead partitions are kept ready; matches the migration's own default.</summary>
    private const int PartitionDaysAhead = 7;

    [LoggerMessage(
        EventId = 6305,
        Level = LogLevel.Information,
        Message = "Retention created {PartitionCount} event partition(s), keeping {DaysAhead} days ready.")]
    private partial void LogPartitionsCreated(int partitionCount, int daysAhead);
}
