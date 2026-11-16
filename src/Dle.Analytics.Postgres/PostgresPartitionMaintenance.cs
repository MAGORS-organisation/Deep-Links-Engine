using Dapper;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Shared helper that creates future daily partitions for partitioned raw-event tables before
/// ingestion reaches midnight UTC.
/// </summary>
internal static partial class PostgresPartitionMaintenance
{
    internal const int DaysAhead = 7;

    internal static async Task<int> EnsureUpcomingAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        ILogger logger,
        CancellationToken ct)
    {
        int created = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT dle_click_events_maintain(@daysAhead, NULL) + dle_sdk_events_maintain(@daysAhead, NULL)",
                new { daysAhead = DaysAhead },
                commandTimeout: commandTimeoutSeconds,
                cancellationToken: ct));

        if (created > 0)
        {
            LogPartitionsCreated(logger, created, DaysAhead);
        }

        return created;
    }

    [LoggerMessage(
        EventId = 6305,
        Level = LogLevel.Information,
        Message = "Partition maintenance created {PartitionCount} event partition(s), keeping {DaysAhead} days ready.")]
    private static partial void LogPartitionsCreated(ILogger logger, int partitionCount, int daysAhead);
}
