using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Brings the analytics rollup schema up to date when the host starts: the rollup and quality
/// tables, the rollup state and retention run ledgers, and the <c>dle_platform_of</c> function the
/// breakdown queries group by.
/// </summary>
/// <remarks>
/// <para>
/// The schema lives in <c>Sql/001_analytics_rollups.sql</c>, outside the EF Core migration set on
/// purpose (ADR-006): the PostgreSQL analytics provider is one of two, and an operator who runs the
/// ClickHouse provider has no use for these tables. Every statement in the script is idempotent
/// (<c>IF NOT EXISTS</c>, <c>OR REPLACE</c>), so running it on every start costs one round trip and
/// changes nothing on a database that already has the schema.
/// </para>
/// <para>
/// A failure is logged and does not stop the host. The control plane still manages links, keys and
/// domains without the rollup schema; what it cannot do is aggregate, and the rollup and retention
/// workers say so on every run until the script is applied by a role that may create tables.
/// </para>
/// </remarks>
internal sealed partial class PostgresAnalyticsSchema : IHostedService
{
    private readonly IAnalyticsConnectionFactory _connections;
    private readonly ILogger<PostgresAnalyticsSchema> _logger;

    /// <summary>Creates the initializer.</summary>
    /// <param name="connections">Connections to the analytics database.</param>
    /// <param name="logger">Logger.</param>
    public PostgresAnalyticsSchema(
        IAnalyticsConnectionFactory connections,
        ILogger<PostgresAnalyticsSchema> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connections.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                AnalyticsSqlScripts.Read(AnalyticsSqlScripts.Migration),
                commandTimeout: _connections.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            LogSchemaReady();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            LogSchemaFailed(exception, AnalyticsSqlScripts.Migration);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 6500,
        Level = LogLevel.Debug,
        Message = "The analytics rollup schema is in place.")]
    private partial void LogSchemaReady();

    [LoggerMessage(
        EventId = 6501,
        Level = LogLevel.Error,
        Message = "The analytics rollup schema could not be applied. Aggregation, retention and "
            + "breakdowns by platform stay unavailable until {Script} is run against the analytics "
            + "database by a role that may create tables.")]
    private partial void LogSchemaFailed(Exception exception, string script);
}
