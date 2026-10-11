using System.Data;

using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Persistence.Fast.Configuration;

using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Dle.Persistence.Fast.Attribution;

/// <summary>
/// Writes issued claim codes to Postgres using parameterized SQL (FR-184).
/// </summary>
public sealed class DapperClaimCodeWriter : IClaimCodeWriter
{
    private const string InsertSql = """
        INSERT INTO claim_codes (id, tenant_id, code_hash, click_id, link_id, expires_at, created_at)
        VALUES (@id, @tenant_id, @code_hash, @click_id, @link_id, @expires_at, @created_at)
        ON CONFLICT (tenant_id, code_hash) DO NOTHING
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;

    /// <summary>Creates the writer.</summary>
    public DapperClaimCodeWriter(NpgsqlDataSource dataSource, FastPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ClaimCodeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();

        command.CommandText = InsertSql;
        command.CommandTimeout = _commandTimeoutSeconds;

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = record.Id == Guid.Empty ? Guid.CreateVersion7() : record.Id });
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = record.TenantId });
        command.Parameters.Add(new NpgsqlParameter("code_hash", NpgsqlDbType.Bytea) { Value = record.CodeHash });
        command.Parameters.Add(new NpgsqlParameter("click_id", NpgsqlDbType.Text) { Value = (object?)record.ClickId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("link_id", NpgsqlDbType.Bigint) { Value = (object?)record.LinkId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = record.ExpiresAt });
        command.Parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = record.CreatedAt });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
