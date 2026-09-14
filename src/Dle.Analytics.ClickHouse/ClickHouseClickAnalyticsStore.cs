using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using ClickHouse.Client.ADO;

using Microsoft.Extensions.Logging;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// <see cref="IClickAnalyticsStore"/> over ClickHouse — the opt-in analytics provider of ADR-006,
/// intended for installations past roughly fifty million events a month.
/// </summary>
/// <remarks>
/// <para>
/// The contract is satisfied identically to the PostgreSQL implementation, which is the whole
/// point of the port: moving provider is an operator decision, not a rewrite. Every query is
/// tenant scoped, every query carries the reported time range so the monthly partitions prune,
/// and crawler traffic is excluded unless <see cref="AnalyticsQuery.IncludeBots"/> is set
/// (FR-205, TC-106).
/// </para>
/// <para>
/// Unlike the PostgreSQL provider this one keeps no rollup tables. ClickHouse aggregates raw
/// events fast enough that a second copy would only add a staleness window and a maintenance job.
/// The schema front-loads the one piece of derivation that must not drift between providers:
/// <c>platform</c> is a materialised column computed at insert time by the same rule the
/// PostgreSQL function <c>dle_platform_of</c> applies.
/// </para>
/// <para>
/// There are two supported ways to fill these tables (§B.5.3 / ADR-006).
/// <see cref="ClickHouseEventSink"/> writes the click stream here directly — instead of
/// PostgreSQL, or alongside it as a dual write during a migration. Alternatively, logical
/// replication from PostgreSQL mirrors the same rows through a change-data-capture pipeline; the
/// mirror tables in the embedded DDL use <c>ReplacingMergeTree</c> with a version column
/// precisely so a replication stream may re-deliver a row without duplicating it.
/// </para>
/// </remarks>
public sealed partial class ClickHouseClickAnalyticsStore : IClickAnalyticsStore
{
    private readonly IClickHouseConnectionFactory _connections;
    private readonly ILogger<ClickHouseClickAnalyticsStore> _logger;

    /// <summary>Creates the store.</summary>
    /// <param name="connections">Factory for ClickHouse connections.</param>
    /// <param name="logger">Logger for query diagnostics.</param>
    public ClickHouseClickAnalyticsStore(
        IClickHouseConnectionFactory connections,
        ILogger<ClickHouseClickAnalyticsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(
        AnalyticsQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        ClickHouseParameterSet parameters = new();
        string bucket = BucketExpression(query.Grain);
        string step = FillStep(query.Grain);
        string clickWhere = BuildClickFilter(query, "e", parameters);
        string installWhere = BuildInstallFilter(query, "i", parameters);
        string conversionWhere = BuildConversionFilter(query, "s", parameters);
        string fillFrom = Bucketed(bucket, "fromUnixTimestamp64Milli({fromMs:Int64}, 'UTC')");

        string sql = $$"""
            SELECT bucket,
                   sum(clicks) AS clicks,
                   sum(installs) AS installs,
                   sum(conversions) AS conversions
            FROM
            (
                SELECT {{Bucketed(bucket, "e.occurred_at")}} AS bucket,
                       toInt64(count()) AS clicks,
                       toInt64(0) AS installs,
                       toInt64(0) AS conversions
                FROM click_events AS e
                WHERE {{clickWhere}}
                GROUP BY bucket
                UNION ALL
                SELECT {{Bucketed(bucket, "i.first_open_at")}} AS bucket,
                       toInt64(0) AS clicks,
                       toInt64(count()) AS installs,
                       toInt64(0) AS conversions
                FROM installs AS i
                WHERE {{installWhere}}
                GROUP BY bucket
                UNION ALL
                SELECT {{Bucketed(bucket, "s.occurred_at")}} AS bucket,
                       toInt64(0) AS clicks,
                       toInt64(0) AS installs,
                       toInt64(count()) AS conversions
                FROM sdk_events AS s
                WHERE {{conversionWhere}}
                GROUP BY bucket
            )
            GROUP BY bucket
            ORDER BY bucket ASC
            WITH FILL FROM {{fillFrom}}
                      TO fromUnixTimestamp64Milli({toMs:Int64}, 'UTC')
                      STEP {{step}}
            """;

        IReadOnlyList<TimeSeriesPoint> points = await QueryAsync(
            sql,
            parameters,
            static reader => new TimeSeriesPoint(
                ReadUtc(reader, 0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)),
            ct);

        LogTimeSeriesCompleted(points.Count, query.Grain);

        return points;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(
        AnalyticsQuery query,
        string dimension,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);

        if (!TryResolveDimension(dimension, out string keyExpression, out bool needsLinkJoin))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                dimension,
                "Unsupported analytics breakdown dimension.");
        }

        ClickHouseParameterSet parameters = new();
        parameters.Add("rowLimit", "UInt32", (uint)Math.Clamp(query.Limit, 1, 10_000));

        string clickWhere = BuildClickFilter(query, "e", parameters);
        string attributedWhere = BuildClickFilter(query, "ce", parameters);
        string clickJoin = needsLinkJoin ? LinkJoin("e") : string.Empty;
        string attributedJoin = needsLinkJoin ? LinkJoin("ce") : string.Empty;

        // Installs and conversions are counted through the click that produced them, so that every
        // dimension groups by exactly the same expression and the three fact sources line up. An
        // install whose click falls outside the reported range is therefore not counted here; the
        // funnel, which counts installs by their own timestamp, is the report that answers that.
        string sql = $$"""
            SELECT key,
                   sum(clicks) AS clicks,
                   sum(installs) AS installs,
                   sum(conversions) AS conversions,
                   sum(conversion_value) AS conversion_value
            FROM
            (
                SELECT {{Keyed(keyExpression, "e")}} AS key,
                       toInt64(count()) AS clicks,
                       toInt64(0) AS installs,
                       toInt64(0) AS conversions,
                       toDecimal128(0, 4) AS conversion_value
                FROM click_events AS e
                {{clickJoin}}
                WHERE {{clickWhere}}
                GROUP BY key
                UNION ALL
                SELECT {{Keyed(keyExpression, "ce")}} AS key,
                       toInt64(0) AS clicks,
                       toInt64(count()) AS installs,
                       toInt64(0) AS conversions,
                       toDecimal128(0, 4) AS conversion_value
                FROM attributions AS a
                INNER JOIN click_events AS ce ON ce.click_id = a.click_id
                {{attributedJoin}}
                WHERE {{attributedWhere}}
                  AND a.tenant_id = toUUID({tenantId:String})
                  AND a.match_type != 'none'
                  AND a.click_id != ''
                GROUP BY key
                UNION ALL
                SELECT {{Keyed(keyExpression, "ce")}} AS key,
                       toInt64(0) AS clicks,
                       toInt64(0) AS installs,
                       toInt64(count()) AS conversions,
                       toDecimal128(sum(ifNull(s.value, toDecimal64(0, 4))), 4) AS conversion_value
                FROM sdk_events AS s
                INNER JOIN click_events AS ce ON ce.click_id = s.click_id
                {{attributedJoin}}
                WHERE {{attributedWhere}}
                  AND s.tenant_id = toUUID({tenantId:String})
                  AND s.type = 'conversion'
                  AND s.click_id != ''
                GROUP BY key
            )
            GROUP BY key
            ORDER BY clicks DESC, key ASC
            LIMIT {rowLimit:UInt32}
            """;

        return await QueryAsync(
            sql,
            parameters,
            static reader =>
            {
                long conversions = reader.GetInt64(3);

                return new BreakdownRow(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    conversions > 0 ? reader.GetDecimal(4) : null);
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<FunnelSummary> GetFunnelAsync(AnalyticsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        ClickHouseParameterSet parameters = new();
        string clickWhere = BuildClickFilter(query, "e", parameters);
        string installWhere = BuildInstallFilter(query, "i", parameters);
        string conversionWhere = BuildConversionFilter(query, "s", parameters);

        string sql = $$"""
            SELECT
                (SELECT toInt64(count()) FROM click_events AS e WHERE {{clickWhere}}) AS clicks,
                (SELECT toInt64(count()) FROM installs AS i WHERE {{installWhere}}) AS installs,
                (
                    SELECT toInt64(count())
                    FROM attributions AS a
                    WHERE a.tenant_id = toUUID({tenantId:String})
                      AND a.match_type != 'none'
                      AND a.install_id IN (SELECT i.id FROM installs AS i WHERE {{installWhere}})
                ) AS attributed,
                (SELECT toInt64(count()) FROM sdk_events AS s WHERE {{conversionWhere}}) AS conversions
            """;

        IReadOnlyList<FunnelSummary> rows = await QueryAsync(
            sql,
            parameters,
            static reader =>
            {
                long clicks = reader.GetInt64(0);
                long installs = reader.GetInt64(1);
                long attributed = Math.Min(reader.GetInt64(2), installs);
                long conversions = reader.GetInt64(3);

                return new FunnelSummary(
                    clicks,
                    installs,
                    attributed,
                    conversions,
                    ConversionRate(clicks, conversions));
            },
            ct);

        return rows.Count > 0 ? rows[0] : new FunnelSummary(0, 0, 0, 0, 0m);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchTypeSummary>> GetMatchTypesAsync(
        AnalyticsQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        ClickHouseParameterSet parameters = new();
        string installWhere = BuildInstallFilter(query, "i", parameters);

        // Installs that carry no attribution row at all are reported as "none" rather than
        // dropped: the unmatched share is the honest half of the ADR-008 dashboard panel.
        string sql = $$"""
            SELECT match_type,
                   toInt64(count()) AS matches,
                   avg(confidence) AS average_confidence
            FROM
            (
                SELECT a.match_type AS match_type, toFloat64(a.confidence) AS confidence
                FROM attributions AS a
                WHERE a.tenant_id = toUUID({tenantId:String})
                  AND a.install_id IN (SELECT i.id FROM installs AS i WHERE {{installWhere}})
                UNION ALL
                SELECT 'none' AS match_type, toFloat64(0) AS confidence
                FROM installs AS i
                WHERE {{installWhere}}
                  AND i.id NOT IN
                  (
                      SELECT a.install_id
                      FROM attributions AS a
                      WHERE a.tenant_id = toUUID({tenantId:String})
                  )
            )
            GROUP BY match_type
            ORDER BY matches DESC, match_type ASC
            """;

        return await QueryAsync(
            sql,
            parameters,
            static reader => new MatchTypeSummary(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2)
                    ? 0m
                    : Math.Round((decimal)reader.GetDouble(2), 2, MidpointRounding.AwayFromZero)),
            ct);
    }

    private static string LinkJoin(string alias) =>
        $"LEFT JOIN links AS l ON l.id = {alias}.link_id AND l.tenant_id = {alias}.tenant_id";

    private static decimal ConversionRate(long clicks, long conversions) => clicks <= 0
        ? 0m
        : Math.Round((decimal)conversions / clicks, 4, MidpointRounding.AwayFromZero);

    private static DateTimeOffset ReadUtc(DbDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>
    /// Wraps a timestamp column in the bucket function chosen for the requested grain.
    /// </summary>
    private static string Bucketed(string bucketFunction, string column) =>
        string.Format(CultureInfo.InvariantCulture, bucketFunction, column);

    /// <summary>Binds a dimension key expression to a table alias.</summary>
    private static string Keyed(string keyExpression, string alias) =>
        string.Format(CultureInfo.InvariantCulture, keyExpression, alias);

    /// <summary>
    /// Bucket function per grain, as a composite format string with a single placeholder for the
    /// timestamp column. Every branch yields <c>DateTime64(3, 'UTC')</c>, so the union of the three
    /// fact sources and the <c>WITH FILL</c> bounds all share one type.
    /// </summary>
    private static string BucketExpression(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "toDateTime64(toStartOfHour({0}), 3, 'UTC')",
        TimeGrain.Week => "toDateTime64(toStartOfWeek({0}, 1), 3, 'UTC')",
        TimeGrain.Month => "toDateTime64(toStartOfMonth({0}), 3, 'UTC')",
        _ => "toDateTime64(toStartOfDay({0}), 3, 'UTC')",
    };

    private static string FillStep(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "INTERVAL 1 HOUR",
        TimeGrain.Week => "INTERVAL 1 WEEK",
        TimeGrain.Month => "INTERVAL 1 MONTH",
        _ => "INTERVAL 1 DAY",
    };

    /// <summary>
    /// Maps a public dimension name to a key expression, as a composite format string whose single
    /// placeholder is the click-event table alias. The allowlist is the whole defence: a dimension
    /// name supplied by a caller never reaches the statement text.
    /// </summary>
    private static bool TryResolveDimension(
        string dimension,
        out string keyExpression,
        out bool needsLinkJoin)
    {
        needsLinkJoin = false;

        switch (dimension.Trim().ToLowerInvariant())
        {
            case "country":
                keyExpression = "if({0}.country = '', 'unknown', {0}.country)";
                return true;
            case "region":
                keyExpression = "if({0}.region = '', 'unknown', {0}.region)";
                return true;
            case "platform":
                keyExpression = "if({0}.platform = '', 'unknown', {0}.platform)";
                return true;
            case "os_family":
                keyExpression = "if({0}.os_family = '', 'unknown', {0}.os_family)";
                return true;
            case "device_class":
                keyExpression = "if({0}.device_class = '', 'unknown', {0}.device_class)";
                return true;
            case "channel":
                keyExpression = "if({0}.channel = '', 'unknown', {0}.channel)";
                return true;
            case "language":
                keyExpression = "if({0}.language = '', 'unknown', {0}.language)";
                return true;
            case "referrer_host":
                keyExpression = "if({0}.referrer_host = '', 'unknown', {0}.referrer_host)";
                return true;
            case "decision":
                keyExpression = "if({0}.decision = '', 'unknown', {0}.decision)";
                return true;
            case "link":
                keyExpression = "toString({0}.link_id)";
                return true;
            case "ab_variant":
                keyExpression = "if({0}.ab_bucket < 0, 'unknown', toString({0}.ab_bucket))";
                return true;
            case "campaign":
                keyExpression =
                    "if(l.campaign_id = toUUID('00000000-0000-0000-0000-000000000000'), " +
                    "'unknown', toString(l.campaign_id))";
                needsLinkJoin = true;
                return true;
            default:
                keyExpression = string.Empty;
                return false;
        }
    }

    private static string BuildClickFilter(
        AnalyticsQuery query,
        string alias,
        ClickHouseParameterSet parameters)
    {
        AddCommonParameters(query, parameters);

        List<string> clauses =
        [
            $"{alias}.tenant_id = toUUID({{tenantId:String}})",
            $"{alias}.occurred_at >= fromUnixTimestamp64Milli({{fromMs:Int64}}, 'UTC')",
            $"{alias}.occurred_at < fromUnixTimestamp64Milli({{toMs:Int64}}, 'UTC')",
        ];

        if (!query.IncludeBots)
        {
            clauses.Add($"{alias}.is_bot = 0");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", "Int64", linkId);
            clauses.Add($"{alias}.link_id = {{linkId:Int64}}");
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            parameters.Add("country", "String", query.Country.Trim().ToUpperInvariant());
            clauses.Add($"{alias}.country = {{country:String}}");
        }

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", "String", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"{alias}.platform = {{platform:String}}");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", "String", campaignId.ToString());
            clauses.Add(
                $"{alias}.link_id IN (SELECT id FROM links " +
                "WHERE tenant_id = toUUID({tenantId:String}) " +
                "AND campaign_id = toUUID({campaignId:String}))");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string BuildInstallFilter(
        AnalyticsQuery query,
        string alias,
        ClickHouseParameterSet parameters)
    {
        AddCommonParameters(query, parameters);

        List<string> clauses =
        [
            $"{alias}.tenant_id = toUUID({{tenantId:String}})",
            $"{alias}.first_open_at >= fromUnixTimestamp64Milli({{fromMs:Int64}}, 'UTC')",
            $"{alias}.first_open_at < fromUnixTimestamp64Milli({{toMs:Int64}}, 'UTC')",
        ];

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", "String", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"lower({alias}.platform) = {{platform:String}}");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", "Int64", linkId);
            clauses.Add(
                $"{alias}.id IN (SELECT install_id FROM attributions " +
                "WHERE tenant_id = toUUID({tenantId:String}) AND link_id = {linkId:Int64})");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", "String", campaignId.ToString());
            clauses.Add(
                $"{alias}.id IN (SELECT install_id FROM attributions " +
                "WHERE tenant_id = toUUID({tenantId:String}) AND link_id IN " +
                "(SELECT id FROM links WHERE tenant_id = toUUID({tenantId:String}) " +
                "AND campaign_id = toUUID({campaignId:String})))");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string BuildConversionFilter(
        AnalyticsQuery query,
        string alias,
        ClickHouseParameterSet parameters)
    {
        AddCommonParameters(query, parameters);

        List<string> clauses =
        [
            $"{alias}.tenant_id = toUUID({{tenantId:String}})",
            $"{alias}.occurred_at >= fromUnixTimestamp64Milli({{fromMs:Int64}}, 'UTC')",
            $"{alias}.occurred_at < fromUnixTimestamp64Milli({{toMs:Int64}}, 'UTC')",
            $"{alias}.type = 'conversion'",
        ];

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", "Int64", linkId);
            clauses.Add($"{alias}.link_id = {{linkId:Int64}}");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", "String", campaignId.ToString());
            clauses.Add(
                $"{alias}.link_id IN (SELECT id FROM links " +
                "WHERE tenant_id = toUUID({tenantId:String}) " +
                "AND campaign_id = toUUID({campaignId:String}))");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static void AddCommonParameters(AnalyticsQuery query, ClickHouseParameterSet parameters)
    {
        parameters.Add("tenantId", "String", query.TenantId.ToString());
        parameters.Add("fromMs", "Int64", query.From.ToUnixTimeMilliseconds());
        parameters.Add("toMs", "Int64", query.To.ToUnixTimeMilliseconds());
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        ClickHouseParameterSet parameters,
        Func<DbDataReader, T> map,
        CancellationToken ct)
    {
        List<T> rows = [];

        await using ClickHouseConnection connection = await _connections.OpenAsync(ct);
        using ClickHouseCommand command = connection.CreateCommand();

        command.CommandTimeout = _connections.CommandTimeoutSeconds;
        SetCommandText(command, sql);
        parameters.ApplyTo(command);

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Debug,
        Message = "ClickHouse time series returned {BucketCount} buckets at grain {Grain}.")]
    private partial void LogTimeSeriesCompleted(int bucketCount, TimeGrain grain);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The statement is assembled from string literals in this file plus fragments taken " +
            "from closed allowlists: the bucket function per TimeGrain, the dimension key " +
            "expression resolved by TryResolveDimension, and fixed table aliases. Every value a " +
            "caller supplies travels as a named ClickHouse server-side parameter through " +
            "ClickHouseParameterSet and is never concatenated into the text.")]
    private static void SetCommandText(ClickHouseCommand command, string sql) =>
        command.CommandText = sql;
}
