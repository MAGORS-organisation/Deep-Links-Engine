using Dle.Persistence.Fast.Data;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// Writes batches of click events with <c>COPY … FROM STDIN (FORMAT BINARY)</c> (§C.3.2, ADR-004).
/// </summary>
/// <remarks>
/// <para>
/// The click stream is the highest-volume table in the product and every row is an insert that nobody
/// reads back for minutes. Binary <c>COPY</c> is the only write path in Postgres that avoids per-row
/// statement overhead entirely, which is what makes the target of §C.3.2 — five thousand rows every
/// 250 ms per instance — reachable on the hardware of §B.8 profile A.
/// </para>
/// <para>
/// The column list, its order and the type of every value have to agree with the DDL of §B.5.3
/// exactly. A binary <c>COPY</c> carries type OIDs, not names: one column in the wrong place or one
/// value written as <c>text</c> into <c>character(2)</c> aborts the whole batch, not the offending row.
/// That is why the country code is normalised to null unless it is exactly two characters, and why the
/// network prefix is parsed into <see cref="NpgsqlInet"/> instead of being handed over as a string.
/// </para>
/// <para>
/// <c>ClickEvent.SpoofedBot</c> has no column of its own in §B.5.3 and is carried in <c>extra</c> under
/// the key <c>spoofed_bot</c>. Adding a column would be a schema change owned by the control plane's
/// migrations, and the flag is rare enough that a JSON key costs nothing.
/// </para>
/// </remarks>
public sealed class CopyClickEventWriter : IClickEventWriter
{
    /// <summary>
    /// The copy statement. Column order mirrors <c>click_events</c> in §B.5.3 top to bottom.
    /// </summary>
    private const string CopyCommand =
        "COPY click_events (id, occurred_at, tenant_id, link_id, click_id, ip_hash, ip_prefix, " +
        "ua_family, os_family, os_version, device_class, country, region, language, referrer_host, " +
        "channel, decision, ab_bucket, consent_mode, is_bot, latency_ms, extra) " +
        "FROM STDIN (FORMAT BINARY)";

    /// <summary>Key used to carry <c>ClickEvent.SpoofedBot</c> inside <c>extra</c>.</summary>
    private const string SpoofedBotKey = "spoofed_bot";

    private const string EmptyJsonObject = "{}";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates the writer.
    /// </summary>
    /// <param name="dataSource">
    /// The primary Postgres pool. Events are never written to a replica, and the pool's command
    /// timeout is the one from <c>Dle:Persistence:Fast:WriteCommandTimeoutSeconds</c>: a binary
    /// <c>COPY</c> has no command object whose timeout could be raised per call, so the batch budget
    /// has to be the pool's default and the short read timeout is set on each read command instead.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public CopyClickEventWriter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<ClickEvent> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(CopyCommand, ct);

        foreach (ClickEvent item in batch)
        {
            await WriteRowAsync(writer, item, ct);
        }

        _ = await writer.CompleteAsync(ct);
    }

    private static async Task WriteRowAsync(NpgsqlBinaryImporter writer, ClickEvent item, CancellationToken ct)
    {
        await writer.StartRowAsync(ct);

        await writer.WriteAsync(item.Id, NpgsqlDbType.Uuid, ct);

        // timestamptz is written from a UTC DateTime: the shared kernel keeps every instant in UTC, and
        // handing Npgsql a DateTimeOffset with a non-zero offset is the one shape it refuses.
        await writer.WriteAsync(item.OccurredAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);

        await writer.WriteAsync(item.TenantId, NpgsqlDbType.Uuid, ct);
        await writer.WriteAsync(item.LinkId, NpgsqlDbType.Bigint, ct);
        await writer.WriteAsync(item.ClickId, NpgsqlDbType.Text, ct);

        await WriteByteaAsync(writer, item.IpHash, ct);
        await WriteInetAsync(writer, item.IpPrefix, ct);

        await WriteTextAsync(writer, item.UaFamily, ct);
        await WriteTextAsync(writer, item.OsFamily, ct);
        await WriteTextAsync(writer, item.OsVersion, ct);
        await WriteTextAsync(writer, item.DeviceClass, ct);

        await WriteFixedWidthAsync(writer, item.Country, 2, ct);

        await WriteTextAsync(writer, item.Region, ct);
        await WriteTextAsync(writer, item.Language, ct);
        await WriteTextAsync(writer, item.ReferrerHost, ct);
        await WriteTextAsync(writer, item.Channel, ct);
        await writer.WriteAsync(item.Decision, NpgsqlDbType.Text, ct);

        await WriteSmallIntAsync(writer, item.AbBucket, ct);

        await writer.WriteAsync(item.ConsentMode, NpgsqlDbType.Text, ct);
        await writer.WriteAsync(item.IsBot, NpgsqlDbType.Boolean, ct);

        await WriteSmallIntAsync(writer, item.LatencyMs, ct);

        await writer.WriteAsync(BuildExtra(item), NpgsqlDbType.Jsonb, ct);
    }

    private static Task WriteTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct) =>
        string.IsNullOrEmpty(value) ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, NpgsqlDbType.Text, ct);

    private static Task WriteFixedWidthAsync(NpgsqlBinaryImporter writer, string? value, int length, CancellationToken ct)
    {
        string? normalized = PostgresValues.FixedWidth(value, length);

        return normalized is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(normalized, NpgsqlDbType.Char, ct);
    }

    private static Task WriteByteaAsync(NpgsqlBinaryImporter writer, byte[]? value, CancellationToken ct) =>
        value is null or { Length: 0 } ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, NpgsqlDbType.Bytea, ct);

    private static Task WriteSmallIntAsync(NpgsqlBinaryImporter writer, short? value, CancellationToken ct) =>
        value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value.Value, NpgsqlDbType.Smallint, ct);

    private static Task WriteInetAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct) =>
        PostgresValues.TryParseInet(value, out NpgsqlInet inet)
            ? writer.WriteAsync(inet, NpgsqlDbType.Inet, ct)
            : writer.WriteNullAsync(ct);

    /// <summary>
    /// Renders <c>extra</c>, folding in the spoofed-bot flag. The column is <c>jsonb NOT NULL</c>, so an
    /// absent map becomes an empty object rather than a null.
    /// </summary>
    private static string BuildExtra(ClickEvent item)
    {
        bool hasExtra = item.Extra is { Count: > 0 };

        if (!hasExtra && !item.SpoofedBot)
        {
            return EmptyJsonObject;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (item.Extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in item.Extra)
            {
                map[pair.Key] = pair.Value;
            }
        }

        if (item.SpoofedBot)
        {
            map[SpoofedBotKey] = "true";
        }

        return JsonSerializer.Serialize(map, DleDomainJsonContext.Default.DictionaryStringString);
    }
}
