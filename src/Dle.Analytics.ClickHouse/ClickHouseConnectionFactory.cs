using ClickHouse.Client.ADO;

using Microsoft.Extensions.Options;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Default <see cref="IClickHouseConnectionFactory"/>: one connection per unit of work over the
/// ClickHouse HTTP interface, which is itself pooled by the driver's shared
/// <see cref="System.Net.Http.HttpClient"/>.
/// </summary>
/// <remarks>
/// The connection string is read from the options snapshot on every call rather than captured in
/// the constructor, so rotating the credential in configuration takes effect on the next query
/// without a restart.
/// </remarks>
public sealed class ClickHouseConnectionFactory : IClickHouseConnectionFactory
{
    private readonly IOptionsMonitor<ClickHouseAnalyticsOptions> _options;

    /// <summary>Creates the factory.</summary>
    /// <param name="options">Monitor over the ClickHouse provider options.</param>
    public ClickHouseConnectionFactory(IOptionsMonitor<ClickHouseAnalyticsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public int CommandTimeoutSeconds => _options.CurrentValue.CommandTimeoutSeconds;

    /// <inheritdoc />
    public async Task<ClickHouseConnection> OpenAsync(CancellationToken ct)
    {
        ClickHouseAnalyticsOptions current = _options.CurrentValue;
        ClickHouseConnection connection = new(current.ConnectionString);

        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }
}
