using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Analytics.Postgres;

/// <summary>
/// Default <see cref="IAnalyticsConnectionFactory"/>: one lazily built <see cref="NpgsqlDataSource"/>
/// over <c>ConnectionStrings:Postgres</c>.
/// </summary>
/// <remarks>
/// The data source is created on first use rather than in the constructor, so registering the
/// analytics module costs nothing at startup and a database that is not reachable yet does not
/// turn into a failed composition. Reporting queries and the maintenance jobs are not on the
/// resolve hot path, so they take their own small pool rather than competing with it.
/// </remarks>
public sealed class NpgsqlAnalyticsConnectionFactory : IAnalyticsConnectionFactory, IAsyncDisposable
{
    private readonly IOptionsMonitor<AnalyticsOptions> _options;
    private readonly Lazy<NpgsqlDataSource> _dataSource;

    /// <summary>Creates the factory.</summary>
    /// <param name="connectionString">Connection string for the analytics database, taken from
    /// <c>ConnectionStrings:Postgres</c>.</param>
    /// <param name="options">Monitor over the analytics options.</param>
    /// <exception cref="ArgumentException">The connection string is missing or blank.</exception>
    public NpgsqlAnalyticsConnectionFactory(
        string connectionString,
        IOptionsMonitor<AnalyticsOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _dataSource = new Lazy<NpgsqlDataSource>(
            () => new NpgsqlDataSourceBuilder(connectionString).Build(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public int CommandTimeoutSeconds => _options.CurrentValue.CommandTimeoutSeconds;

    /// <inheritdoc />
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) =>
        await _dataSource.Value.OpenConnectionAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated)
        {
            await _dataSource.Value.DisposeAsync();
        }
    }
}
