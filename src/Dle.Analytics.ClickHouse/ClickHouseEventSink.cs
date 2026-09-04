using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Batch writer that lands the click stream and the SDK event stream in ClickHouse.
/// </summary>
/// <remarks>
/// <para>
/// This is one of the two supported ways to fill the ClickHouse tables (§B.5.3 / ADR-006). The
/// other is <b>logical replication from PostgreSQL</b>: the engine keeps writing the partitioned
/// PostgreSQL click stream and a change-data-capture pipeline mirrors it here. Replication is the
/// safer path for an installation that is migrating, because PostgreSQL stays the system of
/// record throughout; this sink is the path for an installation that has already decided
/// ClickHouse is where analytics live. The mirror tables in the embedded DDL are
/// <c>ReplacingMergeTree</c> with a version column, so a replication stream may re-deliver a row
/// without duplicating it.
/// </para>
/// <para>
/// Both ports are implemented on one type so that the same connection settings and the same batch
/// size govern both streams. Registering it does not displace another writer: when
/// <c>AddDleAnalytics</c> selects the ClickHouse provider it registers this sink for
/// <see cref="IClickEventWriter"/> and <see cref="ISdkEventWriter"/> in addition to whatever the
/// persistence module registered, so a host that resolves <c>IEnumerable&lt;IClickEventWriter&gt;</c>
/// gets a dual write and a host that resolves the single service gets ClickHouse.
/// </para>
/// <para>
/// Nothing here connects until a batch actually arrives, which is what keeps the assembly inert
/// on an installation that never selected the provider.
/// </para>
/// </remarks>
public sealed partial class ClickHouseEventSink : IClickEventWriter, ISdkEventWriter
{
    private static readonly string[] ClickEventColumns =
    [
        "id", "occurred_at", "tenant_id", "link_id", "click_id", "ip_hash", "ip_prefix",
        "ua_family", "os_family", "os_version", "device_class", "country", "region", "language",
        "referrer_host", "channel", "decision", "ab_bucket", "consent_mode", "is_bot",
        "spoofed_bot", "latency_ms", "extra",
    ];

    private static readonly string[] SdkEventColumns =
    [
        "id", "occurred_at", "tenant_id", "app_id", "install_id", "type", "name", "url", "value",
        "currency", "link_id", "click_id", "properties",
    ];

    private readonly IClickHouseConnectionFactory _connections;
    private readonly IOptionsMonitor<ClickHouseAnalyticsOptions> _options;
    private readonly ILogger<ClickHouseEventSink> _logger;

    /// <summary>Creates the sink.</summary>
    /// <param name="connections">Factory for ClickHouse connections.</param>
    /// <param name="options">Monitor over the ClickHouse provider options.</param>
    /// <param name="logger">Logger for write diagnostics.</param>
    public ClickHouseEventSink(
        IClickHouseConnectionFactory connections,
        IOptionsMonitor<ClickHouseAnalyticsOptions> options,
        ILogger<ClickHouseEventSink> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<ClickEvent> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        long written = await CopyAsync("click_events", ClickEventColumns, Project(batch), ct);

        LogClickEventsWritten(written);
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<SdkEvent> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        long written = await CopyAsync("sdk_events", SdkEventColumns, Project(batch), ct);

        LogSdkEventsWritten(written);
    }

    /// <summary>
    /// Maps <see cref="SdkEventType"/> to the stored lowercase name, matching the wire format
    /// produced by the source-generated JSON context in the shared kernel.
    /// </summary>
    /// <param name="type">The event type to convert.</param>
    /// <returns>The stable stored name.</returns>
    internal static string TypeName(SdkEventType type) => type switch
    {
        SdkEventType.LinkOpen => "link_open",
        SdkEventType.FirstOpen => "first_open",
        SdkEventType.Session => "session",
        SdkEventType.Conversion => "conversion",
        _ => "custom",
    };

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Information,
        Message = "Wrote {RowCount} click events to the ClickHouse click stream.")]
    private partial void LogClickEventsWritten(long rowCount);

    [LoggerMessage(
        EventId = 6011,
        Level = LogLevel.Information,
        Message = "Wrote {RowCount} SDK events to the ClickHouse event stream.")]
    private partial void LogSdkEventsWritten(long rowCount);

    private static IEnumerable<object[]> Project(IReadOnlyList<ClickEvent> batch)
    {
        foreach (ClickEvent e in batch)
        {
            yield return
            [
                e.Id,
                e.OccurredAt.UtcDateTime,
                e.TenantId,
                e.LinkId,
                e.ClickId,
                e.IpHash is null ? string.Empty : Convert.ToHexString(e.IpHash),
                e.IpPrefix ?? string.Empty,
                e.UaFamily ?? string.Empty,
                e.OsFamily ?? string.Empty,
                e.OsVersion ?? string.Empty,
                e.DeviceClass ?? string.Empty,
                e.Country ?? string.Empty,
                e.Region ?? string.Empty,
                e.Language ?? string.Empty,
                e.ReferrerHost ?? string.Empty,
                e.Channel ?? string.Empty,
                e.Decision,
                e.AbBucket ?? (short)-1,
                e.ConsentMode,
                (byte)(e.IsBot ? 1 : 0),
                (byte)(e.SpoofedBot ? 1 : 0),
                e.LatencyMs ?? (short)-1,
                ToMap(e.Extra),
            ];
        }
    }

    private static IEnumerable<object[]> Project(IReadOnlyList<SdkEvent> batch)
    {
        foreach (SdkEvent e in batch)
        {
            yield return
            [
                e.Id,
                e.OccurredAt.UtcDateTime,
                e.TenantId,
                e.AppId,
                e.InstallId,
                TypeName(e.Type),
                e.Name ?? string.Empty,
                e.Url ?? string.Empty,
                e.Value.HasValue ? e.Value.Value : (object)DBNull.Value,
                e.Currency ?? string.Empty,
                e.LinkId ?? 0L,
                e.ClickId ?? string.Empty,
                ToMap(e.Properties),
            ];
        }
    }

    private static Dictionary<string, string> ToMap(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> map = new(source.Count, StringComparer.Ordinal);

        foreach ((string key, string value) in source)
        {
            map[key] = value;
        }

        return map;
    }

    private async Task<long> CopyAsync(
        string destinationTable,
        IReadOnlyCollection<string> columns,
        IEnumerable<object[]> rows,
        CancellationToken ct)
    {
        ClickHouseAnalyticsOptions current = _options.CurrentValue;

        await using ClickHouseConnection connection = await _connections.OpenAsync(ct);
        using ClickHouseBulkCopy copy = new(connection)
        {
            DestinationTableName = destinationTable,
            ColumnNames = columns,
            BatchSize = current.BatchSize,
            MaxDegreeOfParallelism = current.MaxDegreeOfParallelism,
        };

        await copy.InitAsync();
        await copy.WriteToServerAsync(rows, ct);

        return copy.RowsWritten;
    }
}
