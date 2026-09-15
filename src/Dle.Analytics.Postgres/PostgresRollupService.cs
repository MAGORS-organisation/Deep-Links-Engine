using System.Data.Common;

using Dapper;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Maintains the PostgreSQL rollup tables from the partitioned click stream.
/// </summary>
/// <remarks>
/// <para>
/// One pass does two things. It recomputes whole hourly buckets from raw events over a window that
/// starts a few hours before the previous watermark, then folds the complete days of that window
/// into the daily rollups and the attribution-quality rollup. Because buckets are recomputed and
/// overwritten rather than incremented, the overlap costs a little work and buys correctness in
/// the face of late arrivals, retries and crashes.
/// </para>
/// <para>
/// The watermark never covers the last few minutes: the click stream is written from a bounded
/// channel, so the most recent buckets are still filling. Freezing one of those into a rollup and
/// then serving it as complete is the failure mode this design exists to avoid.
/// </para>
/// </remarks>
public sealed partial class PostgresRollupService : IRollupService
{
    private const string ClickHourlyState = "click_rollup_hourly";
    private const string ClickDailyState = "click_rollup_daily";
    private const string InstallHourlyState = "install_rollup_hourly";
    private const string InstallDailyState = "install_rollup_daily";
    private const string AttributionQualityState = "attribution_quality_daily";

    private readonly IAnalyticsConnectionFactory _connections;
    private readonly IOptionsMonitor<AnalyticsOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresRollupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="connections">Factory for analytics database connections.</param>
    /// <param name="options">Monitor over the analytics options.</param>
    /// <param name="timeProvider">Clock. Never <see cref="DateTime.UtcNow"/> (SHARED-KERNEL §17).</param>
    /// <param name="logger">Logger for the job's own audit trail.</param>
    public PostgresRollupService(
        IAnalyticsConnectionFactory connections,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider,
        ILogger<PostgresRollupService> logger)
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
    public async Task<RollupRunResult> RunAsync(CancellationToken ct)
    {
        AnalyticsOptions options = _options.CurrentValue;
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        DateTimeOffset hourlyTarget = TruncateHour(
            startedAt.AddMinutes(-options.RollupLagMinutes));

        IReadOnlyDictionary<string, DateTimeOffset> state = await ReadStateAsync(connection, ct);

        // A stream never aggregated before has no state row, and Earliest() answers MinValue for
        // it: the overlap is stepped back only after the floor has been applied, because
        // MinValue.AddHours(-3) is not a date.
        DateTimeOffset hourlyFloor = hourlyTarget.AddHours(-options.RollupMaxWindowHours);
        DateTimeOffset hourlyFrom = TruncateHour(
            Later(
                Earliest(state, ClickHourlyState, InstallHourlyState),
                hourlyFloor.AddHours(options.RollupOverlapHours))
            .AddHours(-options.RollupOverlapHours));
        DateTimeOffset hourlyThrough = Earlier(
            hourlyFrom.AddHours(options.RollupMaxWindowHours),
            hourlyTarget);

        if (hourlyThrough <= hourlyFrom)
        {
            LogRollupIdle(hourlyTarget);
            return RollupRunResult.Idle(startedAt);
        }

        DateTimeOffset dailyTarget = TruncateDay(hourlyThrough);
        DateTimeOffset dailyFloor = dailyTarget.AddDays(-(options.RollupMaxWindowHours / 24) - 1);
        DateTimeOffset dailyFrom = TruncateDay(
            Later(
                Earliest(state, ClickDailyState, InstallDailyState, AttributionQualityState),
                dailyFloor.AddDays(1))
            .AddDays(-1));
        DateTimeOffset dailyThrough = dailyTarget > dailyFrom ? dailyTarget : dailyFrom;

        int clickHourly;
        int installHourly;
        int clickDaily = 0;
        int installDaily = 0;
        int quality = 0;

        await using (DbTransaction transaction = await connection.BeginTransactionAsync(ct))
        {
            clickHourly = await ExecuteWindowAsync(
                connection, transaction, AnalyticsSqlScripts.RefreshClickRollupHourly,
                hourlyFrom, hourlyThrough, startedAt, ct);

            installHourly = await ExecuteWindowAsync(
                connection, transaction, AnalyticsSqlScripts.RefreshInstallRollupHourly,
                hourlyFrom, hourlyThrough, startedAt, ct);

            await AdvanceAsync(connection, transaction, ClickHourlyState, hourlyThrough, startedAt, ct);
            await AdvanceAsync(connection, transaction, InstallHourlyState, hourlyThrough, startedAt, ct);

            if (dailyThrough > dailyFrom)
            {
                clickDaily = await ExecuteWindowAsync(
                    connection, transaction, AnalyticsSqlScripts.RefreshClickRollupDaily,
                    dailyFrom, dailyThrough, startedAt, ct);

                installDaily = await ExecuteWindowAsync(
                    connection, transaction, AnalyticsSqlScripts.RefreshInstallRollupDaily,
                    dailyFrom, dailyThrough, startedAt, ct);

                quality = await ExecuteWindowAsync(
                    connection, transaction, AnalyticsSqlScripts.RefreshAttributionQualityDaily,
                    dailyFrom, dailyThrough, startedAt, ct);

                await AdvanceAsync(connection, transaction, ClickDailyState, dailyThrough, startedAt, ct);
                await AdvanceAsync(connection, transaction, InstallDailyState, dailyThrough, startedAt, ct);
                await AdvanceAsync(
                    connection, transaction, AttributionQualityState, dailyThrough, startedAt, ct);
            }

            await transaction.CommitAsync(ct);
        }

        DateTimeOffset finishedAt = _timeProvider.GetUtcNow();

        LogRollupCompleted(
            hourlyFrom,
            hourlyThrough,
            clickHourly + installHourly,
            dailyThrough,
            clickDaily + installDaily + quality);

        return new RollupRunResult
        {
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            HourlyFrom = hourlyFrom,
            HourlyThrough = hourlyThrough,
            DailyFrom = dailyFrom,
            DailyThrough = dailyThrough,
            ClickRowsHourly = clickHourly,
            InstallRowsHourly = installHourly,
            ClickRowsDaily = clickDaily,
            InstallRowsDaily = installDaily,
            AttributionQualityRows = quality,
        };
    }

    /// <summary>Truncates an instant down to the start of its UTC hour.</summary>
    /// <param name="value">The instant to truncate.</param>
    /// <returns>The start of the hour containing <paramref name="value"/>.</returns>
    internal static DateTimeOffset TruncateHour(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTimeOffset(
            new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>Truncates an instant down to the start of its UTC day.</summary>
    /// <param name="value">The instant to truncate.</param>
    /// <returns>Midnight UTC of the day containing <paramref name="value"/>.</returns>
    internal static DateTimeOffset TruncateDay(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc));
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    /// <summary>
    /// The oldest watermark among the named rollups. A rollup that has never run has no row, and
    /// is treated as covering nothing — the caller clamps that to the maximum window so the first
    /// pass after installation does not try to aggregate the whole history in one transaction.
    /// </summary>
    private static DateTimeOffset Earliest(
        IReadOnlyDictionary<string, DateTimeOffset> state,
        params string[] names)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;

        foreach (string name in names)
        {
            DateTimeOffset value = state.TryGetValue(name, out DateTimeOffset covered)
                ? covered
                : DateTimeOffset.MinValue;

            if (value < earliest)
            {
                earliest = value;
            }
        }

        return earliest == DateTimeOffset.MaxValue ? DateTimeOffset.MinValue : earliest;
    }

    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadStateAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT name AS "Name", covered_through AS "CoveredThrough"
            FROM analytics_rollup_state
            """;

        IEnumerable<RollupStateRow> rows = await connection.QueryAsync<RollupStateRow>(
            new CommandDefinition(Sql, cancellationToken: ct));

        Dictionary<string, DateTimeOffset> state = new(StringComparer.Ordinal);

        foreach (RollupStateRow row in rows)
        {
            state[row.Name] = new DateTimeOffset(
                DateTime.SpecifyKind(row.CoveredThrough, DateTimeKind.Utc));
        }

        return state;
    }

    private async Task<int> ExecuteWindowAsync(
        NpgsqlConnection connection,
        DbTransaction transaction,
        string scriptName,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        CancellationToken ct)
    {
        CommandDefinition command = new(
            AnalyticsSqlScripts.Read(scriptName),
            new
            {
                from = from.UtcDateTime,
                to = to.UtcDateTime,
                now = now.UtcDateTime,
            },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds,
            cancellationToken: ct);

        return await connection.ExecuteAsync(command);
    }

    private async Task AdvanceAsync(
        NpgsqlConnection connection,
        DbTransaction transaction,
        string name,
        DateTimeOffset coveredThrough,
        DateTimeOffset now,
        CancellationToken ct)
    {
        CommandDefinition command = new(
            AnalyticsSqlScripts.Read(AnalyticsSqlScripts.UpsertRollupState),
            new
            {
                name,
                coveredThrough = coveredThrough.UtcDateTime,
                now = now.UtcDateTime,
            },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds,
            cancellationToken: ct);

        await connection.ExecuteAsync(command);
    }

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Debug,
        Message = "Analytics rollup found nothing to aggregate up to {HourlyTarget:o}.")]
    private partial void LogRollupIdle(DateTimeOffset hourlyTarget);

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Information,
        Message = "Analytics rollup aggregated {HourlyFrom:o}..{HourlyThrough:o} "
                  + "({HourlyRows} hourly rows) and days through {DailyThrough:o} "
                  + "({DailyRows} daily rows).")]
    private partial void LogRollupCompleted(
        DateTimeOffset hourlyFrom,
        DateTimeOffset hourlyThrough,
        int hourlyRows,
        DateTimeOffset dailyThrough,
        int dailyRows);
}
