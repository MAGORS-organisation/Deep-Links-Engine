using Dle.Persistence.Fast.Data;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// Writes batches of SDK reported events with <c>COPY … FROM STDIN (FORMAT BINARY)</c>
/// (<c>POST /v1/events</c>, §B.7.2, §C.3.2).
/// </summary>
/// <remarks>
/// <para>
/// These events matter more than their volume suggests. When an application is already installed, the
/// operating system opens it without ever contacting the edge, so a re-engagement click leaves no trace
/// in the click stream at all; the SDK reporting <c>link_open</c> is the only record of it. Without this
/// path the reporting is systematically wrong in the same direction — the most successful campaigns are
/// the most under-counted (§B.6.4).
/// </para>
/// <para>
/// The endpoint answers <c>202 Accepted</c> before this completes, so durability is this writer's
/// responsibility rather than the caller's: a failure here has to surface as a log entry and a metric,
/// because nothing downstream will notice it.
/// </para>
/// <para>
/// The table this writes into is not spelled out in §B.5.3, which only defines <c>click_events</c>.
/// The column list below is therefore the contract the control plane's migration has to match:
/// <c>sdk_events (id uuid, occurred_at timestamptz, tenant_id uuid, app_id uuid, install_id text,
/// event_type text, event_name text, url text, event_value numeric, currency char(3), link_id bigint,
/// click_id text, properties jsonb)</c>, range partitioned by <c>occurred_at</c> like the click stream.
/// <c>event_type</c>, <c>event_name</c> and <c>event_value</c> avoid <c>type</c>, <c>name</c> and
/// <c>value</c>, which read ambiguously in aggregate queries.
/// </para>
/// </remarks>
public sealed class SdkEventBatchWriter : ISdkEventWriter
{
    /// <summary>The copy statement. Column order is the contract described in the type remarks.</summary>
    private const string CopyCommand =
        "COPY sdk_events (id, occurred_at, tenant_id, app_id, install_id, event_type, event_name, " +
        "url, event_value, currency, link_id, click_id, properties) " +
        "FROM STDIN (FORMAT BINARY)";

    private const string EmptyJsonObject = "{}";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates the writer.
    /// </summary>
    /// <param name="dataSource">The primary Postgres pool; its command timeout bounds one batch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public SdkEventBatchWriter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<SdkEvent> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(CopyCommand, ct);

        foreach (SdkEvent item in batch)
        {
            await WriteRowAsync(writer, item, ct);
        }

        _ = await writer.CompleteAsync(ct);
    }

    private static async Task WriteRowAsync(NpgsqlBinaryImporter writer, SdkEvent item, CancellationToken ct)
    {
        await writer.StartRowAsync(ct);

        await writer.WriteAsync(item.Id, NpgsqlDbType.Uuid, ct);
        await writer.WriteAsync(item.OccurredAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
        await writer.WriteAsync(item.TenantId, NpgsqlDbType.Uuid, ct);
        await writer.WriteAsync(item.AppId, NpgsqlDbType.Uuid, ct);
        await writer.WriteAsync(item.InstallId, NpgsqlDbType.Text, ct);
        await writer.WriteAsync(TypeName(item.Type), NpgsqlDbType.Text, ct);

        await WriteTextAsync(writer, item.Name, ct);
        await WriteTextAsync(writer, item.Url, ct);
        await WriteNumericAsync(writer, item.Value, ct);
        await WriteFixedWidthAsync(writer, item.Currency, 3, ct);
        await WriteBigIntAsync(writer, item.LinkId, ct);
        await WriteTextAsync(writer, item.ClickId, ct);

        await writer.WriteAsync(BuildProperties(item), NpgsqlDbType.Jsonb, ct);
    }

    /// <summary>
    /// Maps the event kind to the text stored in <c>event_type</c>.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the enum, both because a source generated mapping is the
    /// only reflection-free option and because the stored spelling is a wire contract with every report
    /// and dashboard: renaming a member of <see cref="SdkEventType"/> must not silently change data.
    /// </remarks>
    private static string TypeName(SdkEventType type) => type switch
    {
        SdkEventType.LinkOpen => "link_open",
        SdkEventType.FirstOpen => "first_open",
        SdkEventType.Session => "session",
        SdkEventType.Conversion => "conversion",
        SdkEventType.Custom => "custom",
        _ => "custom",
    };

    private static Task WriteTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct) =>
        string.IsNullOrEmpty(value) ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, NpgsqlDbType.Text, ct);

    private static Task WriteNumericAsync(NpgsqlBinaryImporter writer, decimal? value, CancellationToken ct) =>
        value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value.Value, NpgsqlDbType.Numeric, ct);

    private static Task WriteBigIntAsync(NpgsqlBinaryImporter writer, long? value, CancellationToken ct) =>
        value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value.Value, NpgsqlDbType.Bigint, ct);

    private static Task WriteFixedWidthAsync(NpgsqlBinaryImporter writer, string? value, int length, CancellationToken ct)
    {
        string? normalized = PostgresValues.FixedWidth(value, length);

        return normalized is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(normalized, NpgsqlDbType.Char, ct);
    }

    /// <summary>Renders <c>properties</c>; the column is <c>jsonb NOT NULL</c>, so absence is an empty object.</summary>
    private static string BuildProperties(SdkEvent item)
    {
        if (item.Properties is null or { Count: 0 })
        {
            return EmptyJsonObject;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in item.Properties)
        {
            map[pair.Key] = pair.Value;
        }

        return JsonSerializer.Serialize(map, DleDomainJsonContext.Default.DictionaryStringString);
    }
}
