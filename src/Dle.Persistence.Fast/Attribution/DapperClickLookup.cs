using System.Data;
using System.Data.Common;

using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Data;

using Microsoft.Extensions.Logging;

namespace Dle.Persistence.Fast.Attribution;

/// <summary>
/// Reads clicks back out of the partitioned click stream for attribution (§B.6.2, §B.6.3).
/// </summary>
/// <remarks>
/// <para>
/// Both queries read from the primary, not from a replica. Attribution is the one read path where
/// replication lag is not a latency question but a correctness one: a replica that is a few seconds
/// behind reports "no match" for a click that exists, the SDK is told the install was organic, and that
/// attribution is never recovered.
/// </para>
/// <para>
/// Bot traffic is excluded from candidate matching. A crawler's click is a real row in the stream and
/// must stay there for reporting, but attributing a human install to it would be worse than not
/// attributing it at all.
/// </para>
/// </remarks>
public sealed partial class DapperClickLookup : IClickLookup
{
    // The BETWEEN on occurred_at is the whole point of this query, not an optimisation.
    //
    // click_events is RANGE partitioned by occurred_at with one partition per day and a retention of
    // 180 days (§B.5.3). A lookup by click_id alone has no pruning predicate, so the planner has to
    // touch every partition's index — 180 of them at full retention. The cost therefore grows linearly
    // with how long the system has been running and with how old the install is, which means it is
    // invisible in every test written before the first partitions age out and only appears in
    // production about six months after launch. This is the silent performance debt §B.6.3 warns about.
    //
    // The bounds are not guessed by the caller: the click identifier carries its own timestamp, encrypted
    // with the same Feistel construction as the slug, and the attribution service decodes it into
    // occurred_at ± a few minutes before calling here (IClickIdCodec.TryDecode).
    //
    // ip_prefix is projected as host()/masklen() rather than as an inet so that it round trips exactly
    // to the "203.0.113.0/24" spelling IIpHasher.Prefix produces and the candidate filter compares
    // against.
    private const string ByClickIdSql = """
        SELECT ce.id,
               ce.occurred_at,
               ce.tenant_id,
               ce.link_id,
               ce.click_id,
               host(ce.ip_prefix) || '/' || masklen(ce.ip_prefix) AS ip_prefix,
               ce.os_family,
               ce.os_version,
               ce.language,
               ce.country,
               ce.extra::text AS extra
        FROM click_events ce
        WHERE ce.click_id = @click_id
          AND ce.occurred_at BETWEEN @hint_from AND @hint_to
        ORDER BY ce.occurred_at DESC
        LIMIT 1
        """;

    private const string CandidatesSql = """
        SELECT ce.id,
               ce.occurred_at,
               ce.tenant_id,
               ce.link_id,
               ce.click_id,
               host(ce.ip_prefix) || '/' || masklen(ce.ip_prefix) AS ip_prefix,
               ce.os_family,
               ce.os_version,
               ce.language,
               ce.country,
               ce.extra::text AS extra
        FROM click_events ce
        WHERE ce.tenant_id = @tenant_id
          AND ce.occurred_at BETWEEN @from AND @to
          AND ce.is_bot = false
          AND (@ip_prefix IS NULL OR ce.ip_prefix = @ip_prefix::inet)
          AND (@os_family IS NULL OR ce.os_family = @os_family)
        ORDER BY ce.occurred_at DESC
        LIMIT @limit
        """;

    /// <summary>Key under which the resolved deep link path is carried in <c>click_events.extra</c>.</summary>
    private const string DeeplinkPathKey = "deeplink_path";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DapperClickLookup> _logger;
    private readonly int _commandTimeoutSeconds;
    private readonly int _maxCandidateRows;

    /// <summary>
    /// Creates the lookup.
    /// </summary>
    /// <param name="dataSource">The primary Postgres pool.</param>
    /// <param name="options">Hot-path persistence options; supplies the timeout and the candidate cap.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DapperClickLookup(NpgsqlDataSource dataSource, FastPersistenceOptions options, ILogger<DapperClickLookup> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _logger = logger;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        _maxCandidateRows = options.MaxCandidateRows;
    }

    /// <inheritdoc />
    public async ValueTask<ClickRecord?> FindByClickIdAsync(
        string clickId,
        DateTimeOffset hintFrom,
        DateTimeOffset hintTo,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clickId);

        if (hintTo < hintFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(hintTo), "The upper hint must not precede the lower hint.");
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(ByClickIdSql);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<string>("click_id", clickId));
        command.Parameters.Add(new NpgsqlParameter<DateTime>("hint_from", hintFrom.UtcDateTime) { NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<DateTime>("hint_to", hintTo.UtcDateTime) { NpgsqlDbType = NpgsqlDbType.TimestampTz });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);

        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClickRecord>> FindCandidatesAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? ipPrefix,
        string? osFamily,
        CancellationToken ct)
    {
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "The end of the window must not precede its start.");
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(CandidatesSql);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant_id", tenantId));
        command.Parameters.Add(new NpgsqlParameter<DateTime>("from", from.UtcDateTime) { NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<DateTime>("to", to.UtcDateTime) { NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(NullableText("ip_prefix", ipPrefix));
        command.Parameters.Add(NullableText("os_family", osFamily));
        command.Parameters.Add(new NpgsqlParameter<int>("limit", _maxCandidateRows));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);

        var candidates = new List<ClickRecord>();

        while (await reader.ReadAsync(ct))
        {
            candidates.Add(Map(reader));
        }

        return candidates;
    }

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private ClickRecord Map(DbDataReader reader)
    {
        Dictionary<string, string>? extra = ReadExtra(reader, 10);

        return new ClickRecord
        {
            Id = reader.GetGuid(0),
            OccurredAt = reader.GetFieldValue<DateTimeOffset>(1),
            TenantId = reader.GetGuid(2),
            LinkId = reader.GetInt64(3),
            ClickId = reader.GetString(4),
            IpPrefix = PostgresValues.String(reader, 5),
            OsFamily = PostgresValues.String(reader, 6),
            OsVersion = PostgresValues.String(reader, 7),
            Language = PostgresValues.String(reader, 8),
            Country = PostgresValues.String(reader, 9),
            DeeplinkPath = extra is not null && extra.TryGetValue(DeeplinkPathKey, out string? path) ? path : null,
            Extra = extra,
        };
    }

    /// <summary>
    /// Decodes <c>click_events.extra</c>. A document that is not a flat string map is treated as absent
    /// rather than as a failure: the attribution answer is degraded, not lost.
    /// </summary>
    private Dictionary<string, string>? ReadExtra(DbDataReader reader, int ordinal)
    {
        string? json = PostgresValues.String(reader, ordinal);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            Dictionary<string, string>? extra = JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.DictionaryStringString);

            return extra is null || extra.Count == 0 ? null : extra;
        }
        catch (JsonException ex)
        {
            LogMalformedExtra(_logger, ex);
            return null;
        }
    }

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "A click_events.extra document is not a flat string map and was ignored.")]
    private static partial void LogMalformedExtra(ILogger logger, Exception exception);
}
