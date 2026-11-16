using System.Data;
using System.Text.Json;

using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Persistence.Fast.Caching;
using Dle.Persistence.Fast.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Dle.Persistence.Fast.Attribution;

/// <summary>
/// Cached login hint payload stored in Valkey so S2 login reconciliation resolves in <c>O(1)</c>
/// across replicas before hitting PostgreSQL.
/// </summary>
public sealed record LoginHintCacheEntry(
    Guid Id,
    string ClickId,
    long? LinkId,
    string? DeeplinkPath,
    DateTimeOffset OccurredAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// PostgreSQL + optional Valkey write-through store for indexed S2 login reconciliation hints (FR-185).
/// </summary>
public sealed partial class DapperLoginHintStore : ILoginHintStore
{
    private const string InsertSql = """
        INSERT INTO click_login_hints (id, tenant_id, login_key_hash, click_id, link_id, deeplink_path, occurred_at, expires_at)
        VALUES (@id, @tenant_id, @login_key_hash, @click_id, @link_id, @deeplink_path, @occurred_at, @expires_at)
        """;

    private const string SelectLatestSql = """
        SELECT id, click_id, link_id, deeplink_path, occurred_at, expires_at
        FROM click_login_hints
        WHERE tenant_id = @tenant_id
          AND login_key_hash = @login_key_hash
          AND occurred_at >= @min_occurred_at
          AND expires_at > @now
        ORDER BY occurred_at DESC
        LIMIT 1
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<DapperLoginHintStore> _logger;

    /// <summary>Creates the login hint store.</summary>
    /// <param name="dataSource">PostgreSQL connection source.</param>
    /// <param name="options">Fast persistence options.</param>
    /// <param name="redis">Optional shared Valkey multiplexer for write-through <c>O(1)</c> caching.</param>
    /// <param name="logger">Optional logger.</param>
    public DapperLoginHintStore(
        NpgsqlDataSource dataSource,
        FastPersistenceOptions options,
        IConnectionMultiplexer? redis = null,
        ILogger<DapperLoginHintStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        _redis = redis;
        _logger = logger ?? NullLogger<DapperLoginHintStore>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(ClickLoginHint hint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hint);

        Guid id = hint.Id == Guid.Empty ? Guid.CreateVersion7(hint.OccurredAt) : hint.Id;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = InsertSql;
        command.CommandTimeout = _commandTimeoutSeconds;

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = hint.TenantId });
        command.Parameters.Add(new NpgsqlParameter("login_key_hash", NpgsqlDbType.Bytea) { Value = hint.LoginKeyHash });
        command.Parameters.Add(new NpgsqlParameter("click_id", NpgsqlDbType.Varchar) { Value = hint.ClickId });
        command.Parameters.Add(new NpgsqlParameter("link_id", NpgsqlDbType.Bigint) { Value = (object?)hint.LinkId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("deeplink_path", NpgsqlDbType.Varchar) { Value = (object?)hint.DeeplinkPath ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) { Value = hint.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = hint.ExpiresAt });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        TimeSpan ttl = hint.ExpiresAt - hint.OccurredAt;
        if (ttl > TimeSpan.Zero)
        {
            await TryWriteCacheAsync(
                hint.TenantId,
                hint.LoginKeyHash,
                new LoginHintCacheEntry(id, hint.ClickId, hint.LinkId, hint.DeeplinkPath, hint.OccurredAt, hint.ExpiresAt),
                ttl).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ClickLoginHint?> FindLatestAsync(
        Guid tenantId,
        byte[] loginKeyHash,
        DateTimeOffset minOccurredAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loginKeyHash);

        ClickLoginHint? cached = await TryReadCacheAsync(tenantId, loginKeyHash, minOccurredAt, now).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectLatestSql;
        command.CommandTimeout = _commandTimeoutSeconds;

        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter("login_key_hash", NpgsqlDbType.Bytea) { Value = loginKeyHash });
        command.Parameters.Add(new NpgsqlParameter("min_occurred_at", NpgsqlDbType.TimestampTz) { Value = minOccurredAt });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        Guid id = reader.GetGuid(0);
        string clickId = reader.GetString(1);
        long? linkId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        string? deeplinkPath = reader.IsDBNull(3) ? null : reader.GetString(3);
        DateTimeOffset occurredAt = reader.GetFieldValue<DateTimeOffset>(4);
        DateTimeOffset expiresAt = reader.GetFieldValue<DateTimeOffset>(5);

        ClickLoginHint found = new()
        {
            Id = id,
            TenantId = tenantId,
            LoginKeyHash = [.. loginKeyHash],
            ClickId = clickId,
            LinkId = linkId,
            DeeplinkPath = deeplinkPath,
            OccurredAt = occurredAt,
            ExpiresAt = expiresAt,
        };

        TimeSpan remaining = expiresAt - now;
        if (remaining > TimeSpan.Zero)
        {
            await TryWriteCacheAsync(
                tenantId,
                loginKeyHash,
                new LoginHintCacheEntry(id, clickId, linkId, deeplinkPath, occurredAt, expiresAt),
                remaining).ConfigureAwait(false);
        }

        return found;
    }

    private async ValueTask<ClickLoginHint?> TryReadCacheAsync(
        Guid tenantId,
        byte[] loginKeyHash,
        DateTimeOffset minOccurredAt,
        DateTimeOffset now)
    {
        if (_redis is not { IsConnected: true })
        {
            return null;
        }

        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue raw = await db.StringGetAsync(CacheKey(tenantId, loginKeyHash)).ConfigureAwait(false);
            if (!raw.HasValue)
            {
                return null;
            }

            LoginHintCacheEntry? entry = JsonSerializer.Deserialize(
                (string)raw!,
                FastPersistenceJsonContext.Default.LoginHintCacheEntry);

            if (entry is null || entry.ExpiresAt <= now || entry.OccurredAt < minOccurredAt)
            {
                return null;
            }

            return new ClickLoginHint
            {
                Id = entry.Id,
                TenantId = tenantId,
                LoginKeyHash = [.. loginKeyHash],
                ClickId = entry.ClickId,
                LinkId = entry.LinkId,
                DeeplinkPath = entry.DeeplinkPath,
                OccurredAt = entry.OccurredAt,
                ExpiresAt = entry.ExpiresAt,
            };
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            LogCacheWarning(_logger, ex);
            return null;
        }
    }

    private async ValueTask TryWriteCacheAsync(
        Guid tenantId,
        byte[] loginKeyHash,
        LoginHintCacheEntry entry,
        TimeSpan ttl)
    {
        if (_redis is not { IsConnected: true })
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(entry, FastPersistenceJsonContext.Default.LoginHintCacheEntry);
            IDatabase db = _redis.GetDatabase();
            _ = await db.StringSetAsync(CacheKey(tenantId, loginKeyHash), json, ttl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogCacheWarning(_logger, ex);
        }
    }

    private static string CacheKey(Guid tenantId, byte[] loginKeyHash) =>
        string.Concat("dle:lhint:", tenantId.ToString("N"), ":", Convert.ToHexStringLower(loginKeyHash));

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Valkey login-hint cache operation failed; continuing against PostgreSQL.")]
    private static partial void LogCacheWarning(ILogger logger, Exception exception);
}
