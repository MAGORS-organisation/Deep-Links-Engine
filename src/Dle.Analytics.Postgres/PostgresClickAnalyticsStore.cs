using Dapper;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Default <see cref="IClickAnalyticsStore"/> (ADR-006): Dapper over the partitioned
/// <c>click_events</c> table and the rollup tables this module ships.
/// </summary>
/// <remarks>
/// <para>
/// Three properties hold for every query here, and they are the reason the class is worth reading
/// before changing. Every query carries the reported time range, so the daily partitions of
/// <c>click_events</c> prune instead of being scanned back to the beginning of the installation.
/// Every query is scoped by <see cref="AnalyticsQuery.TenantId"/> — there is no code path that
/// reads another tenant's rows. And crawler traffic is excluded unless
/// <see cref="AnalyticsQuery.IncludeBots"/> is set, so a link-preview fetch never inflates a
/// campaign (FR-205, TC-106).
/// </para>
/// <para>
/// Rollups are used, not assumed. A rollup answers a query only when the requested bounds fall
/// exactly on its bucket boundaries and the whole range lies at or before the watermark the rollup
/// service published; otherwise the same report is computed from raw events. Serving a half-filled
/// bucket as though it were complete would be worse than being slow.
/// </para>
/// </remarks>
public sealed partial class PostgresClickAnalyticsStore : IClickAnalyticsStore
{
    private const string ClickRollupHourly = "click_rollup_hourly";
    private const string ClickRollupDaily = "click_rollup_daily";
    private const string InstallRollupHourly = "install_rollup_hourly";
    private const string InstallRollupDaily = "install_rollup_daily";
    private const string AttributionQualityDaily = "attribution_quality_daily";

    private readonly IAnalyticsConnectionFactory _connections;
    private readonly IOptionsMonitor<AnalyticsOptions> _options;
    private readonly ILogger<PostgresClickAnalyticsStore> _logger;

    /// <summary>Creates the store.</summary>
    /// <param name="connections">Factory for analytics database connections.</param>
    /// <param name="options">Monitor over the analytics options.</param>
    /// <param name="logger">Logger for query diagnostics.</param>
    public PostgresClickAnalyticsStore(
        IAnalyticsConnectionFactory connections,
        IOptionsMonitor<AnalyticsOptions> options,
        ILogger<PostgresClickAnalyticsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(
        AnalyticsQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        DynamicParameters parameters = CommonParameters(query);
        parameters.Add("grain", TruncField(query.Grain));
        parameters.Add("step", StepInterval(query.Grain));

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        bool hourly = query.Grain == TimeGrain.Hour;
        string clickRollup = hourly ? ClickRollupHourly : ClickRollupDaily;
        string installRollup = hourly ? InstallRollupHourly : InstallRollupDaily;

        bool useRollup = await CanUseRollupAsync(
            connection,
            query,
            hourly ? TimeGrain.Hour : TimeGrain.Day,
            query.Country is null,
            [clickRollup, installRollup],
            ct);

        string facts = useRollup
            ? $"""
                SELECT date_trunc(@grain, r.bucket, 'UTC') AS bucket,
                       sum(r.clicks)::bigint  AS clicks,
                       0::bigint              AS installs,
                       0::bigint              AS conversions
                FROM {clickRollup} AS r
                WHERE {RollupClickFilter(query, "r", parameters)}
                GROUP BY 1
                UNION ALL
                SELECT date_trunc(@grain, i.bucket, 'UTC') AS bucket,
                       0::bigint                 AS clicks,
                       sum(i.installs)::bigint   AS installs,
                       sum(i.conversions)::bigint AS conversions
                FROM {installRollup} AS i
                WHERE {RollupInstallFilter(query, "i", parameters)}
                GROUP BY 1
                """
            : $"""
                SELECT date_trunc(@grain, e.occurred_at, 'UTC') AS bucket,
                       count(*)::bigint AS clicks,
                       0::bigint        AS installs,
                       0::bigint        AS conversions
                FROM click_events AS e
                {RawClickJoin(query, "e", needsCampaign: query.CampaignId is not null)}
                WHERE {RawClickFilter(query, "e", parameters)}
                GROUP BY 1
                UNION ALL
                SELECT date_trunc(@grain, ins.first_open_at, 'UTC') AS bucket,
                       0::bigint        AS clicks,
                       count(*)::bigint AS installs,
                       0::bigint        AS conversions
                FROM installs AS ins
                WHERE {RawInstallFilter(query, "ins", parameters)}
                GROUP BY 1
                UNION ALL
                SELECT date_trunc(@grain, s.occurred_at, 'UTC') AS bucket,
                       0::bigint        AS clicks,
                       0::bigint        AS installs,
                       count(*)::bigint AS conversions
                FROM sdk_events AS s
                WHERE {RawConversionFilter(query, "s", parameters)}
                GROUP BY 1
                """;

        // Empty buckets are generated rather than omitted: a chart must not have to guess whether
        // a gap means "no data" or "no traffic" (TimeSeriesPoint).
        string sql = $"""
            WITH buckets AS (
                SELECT generate_series(
                    date_trunc(@grain, @from, 'UTC'),
                    @to - interval '1 microsecond',
                    @step::interval) AS bucket
            ),
            facts AS (
            {facts}
            )
            SELECT b.bucket                              AS "Bucket",
                   coalesce(sum(f.clicks), 0)::bigint     AS "Clicks",
                   coalesce(sum(f.installs), 0)::bigint   AS "Installs",
                   coalesce(sum(f.conversions), 0)::bigint AS "Conversions"
            FROM buckets AS b
            LEFT JOIN facts AS f ON f.bucket = b.bucket
            GROUP BY b.bucket
            ORDER BY b.bucket
            """;

        IEnumerable<TimeSeriesRow> rows = await connection.QueryAsync<TimeSeriesRow>(
            Command(sql, parameters, ct));

        List<TimeSeriesPoint> points = [];

        foreach (TimeSeriesRow row in rows)
        {
            points.Add(new TimeSeriesPoint(
                Utc(row.Bucket),
                row.Clicks,
                row.Installs,
                row.Conversions));
        }

        LogTimeSeriesCompleted(points.Count, query.Grain, useRollup);
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

        if (!AnalyticsDimensions.TryResolve(dimension, out AnalyticsDimension? resolved))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                dimension,
                "Unsupported analytics breakdown dimension.");
        }

        DynamicParameters parameters = CommonParameters(query);
        parameters.Add(
            "rowLimit",
            Math.Clamp(query.Limit, 1, _options.CurrentValue.MaxBreakdownRows));

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        bool useRollup = resolved.RollupExpression is not null
            && await CanUseRollupAsync(
                connection,
                query,
                TimeGrain.Hour,
                allowed: true,
                [ClickRollupHourly],
                ct);

        string clickFacts = useRollup
            ? $"""
                SELECT {Bind(resolved.RollupExpression!, "r")} AS key,
                       sum(r.clicks)::bigint AS clicks,
                       0::bigint             AS installs,
                       0::bigint             AS conversions,
                       0::numeric(18, 4)     AS conversion_value
                FROM {ClickRollupHourly} AS r
                WHERE {RollupClickFilter(query, "r", parameters)}
                GROUP BY 1
                """
            : $"""
                SELECT {Bind(resolved.RawExpression, "e")} AS key,
                       count(*)::bigint  AS clicks,
                       0::bigint         AS installs,
                       0::bigint         AS conversions,
                       0::numeric(18, 4) AS conversion_value
                FROM click_events AS e
                {RawClickJoin(query, "e", resolved.NeedsLinkJoin)}
                WHERE {RawClickFilter(query, "e", parameters)}
                GROUP BY 1
                """;

        // Installs and conversions are counted through the click that produced them, so that all
        // three columns group by the same expression. An install whose click falls outside the
        // reported range is therefore not counted here; the funnel, which counts installs by their
        // own timestamp, is the report that answers that question.
        string sql = $"""
            WITH facts AS (
            {clickFacts}
                UNION ALL
                SELECT {Bind(resolved.RawExpression, "ce")} AS key,
                       0::bigint         AS clicks,
                       count(*)::bigint  AS installs,
                       0::bigint         AS conversions,
                       0::numeric(18, 4) AS conversion_value
                FROM attributions AS a
                JOIN click_events AS ce ON ce.click_id = a.click_id
                {RawClickJoin(query, "ce", resolved.NeedsLinkJoin)}
                WHERE {RawClickFilter(query, "ce", parameters)}
                  AND a.tenant_id = @tenantId
                  AND a.match_type <> 'none'
                  AND a.click_id IS NOT NULL
                GROUP BY 1
                UNION ALL
                SELECT {Bind(resolved.RawExpression, "ce")} AS key,
                       0::bigint        AS clicks,
                       0::bigint        AS installs,
                       count(*)::bigint AS conversions,
                       coalesce(sum(s.event_value), 0)::numeric(18, 4) AS conversion_value
                FROM sdk_events AS s
                JOIN click_events AS ce ON ce.click_id = s.click_id
                {RawClickJoin(query, "ce", resolved.NeedsLinkJoin)}
                WHERE {RawClickFilter(query, "ce", parameters)}
                  AND s.tenant_id = @tenantId
                  AND s.event_type = 'conversion'
                  AND s.click_id IS NOT NULL
                GROUP BY 1
            )
            SELECT key                                     AS "Key",
                   sum(clicks)::bigint                     AS "Clicks",
                   sum(installs)::bigint                   AS "Installs",
                   sum(conversions)::bigint                AS "Conversions",
                   sum(conversion_value)::numeric(18, 4)   AS "ConversionValue"
            FROM facts
            GROUP BY key
            ORDER BY sum(clicks) DESC, key ASC
            LIMIT @rowLimit
            """;

        IEnumerable<BreakdownRawRow> rows = await connection.QueryAsync<BreakdownRawRow>(
            Command(sql, parameters, ct));

        List<BreakdownRow> result = [];

        foreach (BreakdownRawRow row in rows)
        {
            result.Add(new BreakdownRow(
                row.Key,
                row.Clicks,
                row.Installs,
                row.Conversions > 0 ? row.ConversionValue : null));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<FunnelSummary> GetFunnelAsync(AnalyticsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        DynamicParameters parameters = CommonParameters(query);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        bool useRollup = await CanUseRollupAsync(
            connection,
            query,
            TimeGrain.Day,
            query.Country is null,
            [ClickRollupDaily, InstallRollupDaily],
            ct);

        string sql = useRollup
            ? $"""
                SELECT
                    (SELECT coalesce(sum(r.clicks), 0)::bigint
                     FROM {ClickRollupDaily} AS r
                     WHERE {RollupClickFilter(query, "r", parameters)})       AS "Clicks",
                    (SELECT coalesce(sum(i.installs), 0)::bigint
                     FROM {InstallRollupDaily} AS i
                     WHERE {RollupInstallFilter(query, "i", parameters)})     AS "Installs",
                    (SELECT coalesce(sum(i.attributed), 0)::bigint
                     FROM {InstallRollupDaily} AS i
                     WHERE {RollupInstallFilter(query, "i", parameters)})     AS "Attributed",
                    (SELECT coalesce(sum(i.conversions), 0)::bigint
                     FROM {InstallRollupDaily} AS i
                     WHERE {RollupInstallFilter(query, "i", parameters)})     AS "Conversions"
                """
            : $"""
                SELECT
                    (SELECT count(*)::bigint
                     FROM click_events AS e
                     {RawClickJoin(query, "e", query.CampaignId is not null)}
                     WHERE {RawClickFilter(query, "e", parameters)})          AS "Clicks",
                    (SELECT count(*)::bigint
                     FROM installs AS ins
                     WHERE {RawInstallFilter(query, "ins", parameters)})      AS "Installs",
                    (SELECT count(*)::bigint
                     FROM attributions AS a
                     WHERE a.tenant_id = @tenantId
                       AND a.match_type <> 'none'
                       AND a.install_id IN (SELECT ins.id FROM installs AS ins
                                            WHERE {RawInstallFilter(query, "ins", parameters)}))
                                                                              AS "Attributed",
                    (SELECT count(*)::bigint
                     FROM sdk_events AS s
                     WHERE {RawConversionFilter(query, "s", parameters)})     AS "Conversions"
                """;

        FunnelRow? row = await connection.QuerySingleOrDefaultAsync<FunnelRow>(
            Command(sql, parameters, ct));

        if (row is null)
        {
            return new FunnelSummary(0, 0, 0, 0, 0m);
        }

        long attributed = Math.Min(row.Attributed, row.Installs);

        return new FunnelSummary(
            row.Clicks,
            row.Installs,
            attributed,
            row.Conversions,
            ConversionRate(row.Clicks, row.Conversions));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchTypeSummary>> GetMatchTypesAsync(
        AnalyticsQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        DynamicParameters parameters = CommonParameters(query);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        bool useRollup = await CanUseRollupAsync(
            connection,
            query,
            TimeGrain.Day,
            query.Country is null && query.Platform is null,
            [AttributionQualityDaily],
            ct);

        // Installs carrying no attribution row at all are reported as "none" rather than dropped.
        // That unmatched share is the honest half of the ADR-008 dashboard panel, and it is
        // exactly the number a commercial measurement partner has an incentive to lose.
        string sql = useRollup
            ? $"""
                SELECT q.match_type                                          AS "MatchType",
                       sum(q.matches)::bigint                                AS "Count",
                       round(sum(q.confidence_sum) / greatest(sum(q.matches), 1), 2)
                                                                             AS "AverageConfidence"
                FROM {AttributionQualityDaily} AS q
                WHERE {QualityFilter(query, "q", parameters)}
                GROUP BY q.match_type
                ORDER BY sum(q.matches) DESC, q.match_type ASC
                """
            : $"""
                WITH source AS (
                    SELECT coalesce(a.match_type, '{MatchTypeNames.None}')   AS match_type,
                           coalesce(a.confidence, 0)::numeric(4, 2)          AS confidence
                    FROM installs AS ins
                    LEFT JOIN attributions AS a ON a.install_id = ins.id
                    LEFT JOIN links AS l ON l.id = a.link_id
                    WHERE {RawInstallFilter(query, "ins", parameters)}
                      {RawAttributionScope(query)}
                )
                SELECT match_type            AS "MatchType",
                       count(*)::bigint      AS "Count",
                       round(avg(confidence), 2) AS "AverageConfidence"
                FROM source
                GROUP BY match_type
                ORDER BY count(*) DESC, match_type ASC
                """;

        IEnumerable<MatchTypeRow> rows = await connection.QueryAsync<MatchTypeRow>(
            Command(sql, parameters, ct));

        List<MatchTypeSummary> result = [];

        foreach (MatchTypeRow row in rows)
        {
            result.Add(new MatchTypeSummary(row.MatchType, row.Count, row.AverageConfidence));
        }

        return result;
    }

    /// <summary>Binds a dimension expression to a table alias.</summary>
    internal static string Bind(string expression, string alias) =>
        string.Format(CultureInfo.InvariantCulture, expression, alias);

    /// <summary><c>date_trunc</c> field name for a grain.</summary>
    internal static string TruncField(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "hour",
        TimeGrain.Week => "week",
        TimeGrain.Month => "month",
        _ => "day",
    };

    /// <summary>Interval literal that advances one bucket of a grain.</summary>
    internal static string StepInterval(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "1 hour",
        TimeGrain.Week => "1 week",
        TimeGrain.Month => "1 month",
        _ => "1 day",
    };

    /// <summary>
    /// Whether an instant sits exactly on a bucket boundary of the given grain, in UTC.
    /// </summary>
    /// <param name="value">The instant to test.</param>
    /// <param name="grain">The bucket size.</param>
    /// <returns><see langword="true"/> when a rollup of that grain can answer a range ending here
    /// without splitting a bucket.</returns>
    internal static bool IsAligned(DateTimeOffset value, TimeGrain grain)
    {
        DateTime utc = value.UtcDateTime;

        if (utc.Ticks % TimeSpan.TicksPerHour != 0)
        {
            return false;
        }

        return grain switch
        {
            TimeGrain.Hour => true,
            TimeGrain.Day => utc.Hour == 0,
            TimeGrain.Week => utc.Hour == 0 && utc.DayOfWeek == DayOfWeek.Monday,
            TimeGrain.Month => utc.Hour == 0 && utc.Day == 1,
            _ => false,
        };
    }

    private static decimal ConversionRate(long clicks, long conversions) => clicks <= 0
        ? 0m
        : Math.Round((decimal)conversions / clicks, 4, MidpointRounding.AwayFromZero);

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DynamicParameters CommonParameters(AnalyticsQuery query)
    {
        DynamicParameters parameters = new();
        parameters.Add("tenantId", query.TenantId);
        parameters.Add("from", query.From.UtcDateTime);
        parameters.Add("to", query.To.UtcDateTime);
        return parameters;
    }

    private CommandDefinition Command(
        string sql,
        DynamicParameters parameters,
        CancellationToken ct) =>
        new(
            sql,
            parameters,
            commandTimeout: _connections.CommandTimeoutSeconds,
            cancellationToken: ct);

    private static string RawClickJoin(AnalyticsQuery query, string alias, bool needsCampaign) =>
        needsCampaign || query.CampaignId is not null
            ? $"LEFT JOIN links AS l ON l.id = {alias}.link_id"
            : string.Empty;

    private static string RawClickFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.occurred_at >= @from",
            $"{alias}.occurred_at < @to",
        ];

        if (!query.IncludeBots)
        {
            clauses.Add($"{alias}.is_bot = false");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add($"{alias}.link_id = @linkId");
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            parameters.Add("country", query.Country.Trim().ToUpperInvariant());
            clauses.Add($"{alias}.country = @country");
        }

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"dle_platform_of({alias}.os_family, {alias}.device_class) = @platform");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add("l.campaign_id = @campaignId");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string RawInstallFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.first_open_at >= @from",
            $"{alias}.first_open_at < @to",
        ];

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"lower({alias}.platform) = @platform");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add(
                $"{alias}.id IN (SELECT att.install_id FROM attributions AS att "
                + "WHERE att.tenant_id = @tenantId AND att.link_id = @linkId)");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add(
                $"{alias}.id IN (SELECT att.install_id FROM attributions AS att "
                + "JOIN links AS cl ON cl.id = att.link_id "
                + "WHERE att.tenant_id = @tenantId AND cl.campaign_id = @campaignId)");
        }

        return string.Join("\n  AND ", clauses);
    }

    /// <summary>
    /// Extra predicates applied to the attribution side of the raw match-type query, where the
    /// join to attributions and links already exists.
    /// </summary>
    private static string RawAttributionScope(AnalyticsQuery query)
    {
        List<string> clauses = [];

        if (query.LinkId is not null)
        {
            clauses.Add("AND (a.link_id IS NULL OR a.link_id = @linkId)");
        }

        if (query.CampaignId is not null)
        {
            clauses.Add("AND (l.campaign_id IS NULL OR l.campaign_id = @campaignId)");
        }

        return clauses.Count == 0 ? string.Empty : string.Join("\n      ", clauses);
    }

    private static string RawConversionFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.occurred_at >= @from",
            $"{alias}.occurred_at < @to",
            $"{alias}.event_type = 'conversion'",
        ];

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add($"{alias}.link_id = @linkId");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add(
                $"{alias}.link_id IN (SELECT cl.id FROM links AS cl "
                + "WHERE cl.tenant_id = @tenantId AND cl.campaign_id = @campaignId)");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string RollupClickFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.bucket >= @from",
            $"{alias}.bucket < @to",
        ];

        if (!query.IncludeBots)
        {
            clauses.Add($"{alias}.is_bot = false");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add($"{alias}.link_id = @linkId");
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            parameters.Add("country", query.Country.Trim().ToUpperInvariant());
            clauses.Add($"{alias}.country = @country");
        }

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"{alias}.platform = @platform");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add($"{alias}.campaign_id = @campaignId");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string RollupInstallFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.bucket >= @from",
            $"{alias}.bucket < @to",
        ];

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add($"{alias}.link_id = @linkId");
        }

        if (!string.IsNullOrWhiteSpace(query.Platform))
        {
            parameters.Add("platform", query.Platform.Trim().ToLowerInvariant());
            clauses.Add($"{alias}.platform = @platform");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add($"{alias}.campaign_id = @campaignId");
        }

        return string.Join("\n  AND ", clauses);
    }

    private static string QualityFilter(
        AnalyticsQuery query,
        string alias,
        DynamicParameters parameters)
    {
        List<string> clauses =
        [
            $"{alias}.tenant_id = @tenantId",
            $"{alias}.bucket >= @from",
            $"{alias}.bucket < @to",
        ];

        if (query.LinkId is { } linkId)
        {
            parameters.Add("linkId", linkId);
            clauses.Add($"{alias}.link_id = @linkId");
        }

        if (query.CampaignId is { } campaignId)
        {
            parameters.Add("campaignId", campaignId);
            clauses.Add(
                $"{alias}.link_id IN (SELECT cl.id FROM links AS cl "
                + "WHERE cl.tenant_id = @tenantId AND cl.campaign_id = @campaignId)");
        }

        return string.Join("\n  AND ", clauses);
    }

    /// <summary>
    /// Decides whether a rollup may answer this query: the module must allow it, the requested
    /// bounds must sit on the rollup's bucket boundaries, the query must not filter on a dimension
    /// the rollup does not carry, and every named rollup must already cover the whole range.
    /// </summary>
    private async Task<bool> CanUseRollupAsync(
        NpgsqlConnection connection,
        AnalyticsQuery query,
        TimeGrain rollupGrain,
        bool allowed,
        IReadOnlyList<string> rollupNames,
        CancellationToken ct)
    {
        if (!allowed
            || !_options.CurrentValue.UseRollups
            || !IsAligned(query.From, rollupGrain)
            || !IsAligned(query.To, rollupGrain))
        {
            return false;
        }

        const string Sql = """
            SELECT name AS "Name", covered_through AS "CoveredThrough"
            FROM analytics_rollup_state
            WHERE name = ANY(@names)
            """;

        DynamicParameters parameters = new();
        parameters.Add("names", rollupNames.ToArray());

        IEnumerable<RollupStateRow> states = await connection.QueryAsync<RollupStateRow>(
            new CommandDefinition(Sql, parameters, cancellationToken: ct));

        Dictionary<string, DateTime> covered = new(StringComparer.Ordinal);

        foreach (RollupStateRow state in states)
        {
            covered[state.Name] = state.CoveredThrough;
        }

        foreach (string name in rollupNames)
        {
            if (!covered.TryGetValue(name, out DateTime through)
                || Utc(through) < query.To)
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Debug,
        Message = "Analytics time series returned {BucketCount} buckets at grain {Grain} "
                  + "(served from rollup: {FromRollup}).")]
    private partial void LogTimeSeriesCompleted(int bucketCount, TimeGrain grain, bool fromRollup);
}
