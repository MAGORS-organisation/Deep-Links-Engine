using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Creates opened PostgreSQL connections for the analytics reporting queries and the rollup and
/// retention jobs.
/// </summary>
/// <remarks>
/// The indirection keeps composition free of I/O: the factory is constructed during registration
/// but opens nothing until a report or a job runs. A host that already owns an
/// <see cref="NpgsqlDataSource"/> can register its own implementation before calling
/// <c>AddDleAnalytics</c> and share the pool, because the default registration uses <c>TryAdd</c>.
/// </remarks>
public interface IAnalyticsConnectionFactory
{
    /// <summary>Command timeout applied to reporting queries and maintenance jobs, in seconds.</summary>
    int CommandTimeoutSeconds { get; }

    /// <summary>Opens a connection to the analytics database.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opened connection. The caller owns it and must dispose it.</returns>
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct);
}
