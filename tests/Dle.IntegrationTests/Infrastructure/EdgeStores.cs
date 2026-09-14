using Dle.Domain.Ports;
using Dle.Persistence.Fast.Attribution;
using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Data;
using Dle.Persistence.Fast.Links;
using Dle.Persistence.Fast.Telemetry;
using Dle.Persistence.Fast.WellKnown;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The hot-path stores of <c>Dle.Persistence.Fast</c>, built directly over a test database.
/// </summary>
/// <remarks>
/// These are the three types the resolve and attribution paths actually query through, so a test
/// that wants to assert on the SQL — the <c>citext</c> casts, the lateral joins, the partition
/// pruning hints — goes through them rather than through a copy of the query. Starting a whole edge
/// host for that would be an order of magnitude more expensive and would prove less: the host would
/// answer from cache.
/// </remarks>
public sealed class EdgeStores : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DleReadDataSource _readDataSource;

    private EdgeStores(NpgsqlDataSource dataSource, FastPersistenceOptions options)
    {
        _dataSource = dataSource;
        _readDataSource = new DleReadDataSource(dataSource, isReplica: false);

        Links = new DapperLinkStore(_readDataSource, options);
        Domains = new DapperDomainConfigStore(_readDataSource, options);
        Clicks = new DapperClickLookup(dataSource, options, NullLogger<DapperClickLookup>.Instance);
        ClickWriter = new CopyClickEventWriter(dataSource);
    }

    /// <summary>The single query behind the resolve path.</summary>
    public ILinkStore Links { get; }

    /// <summary>Per-host configuration and the two association documents.</summary>
    public IDomainConfigStore Domains { get; }

    /// <summary>Click lookups for attribution, with the partition pruning hints of §B.6.3.</summary>
    public IClickLookup Clicks { get; }

    /// <summary>The binary <c>COPY</c> writer of §C.3.2.</summary>
    public IClickEventWriter ClickWriter { get; }

    /// <summary>Opens the stores over a test database.</summary>
    /// <param name="database">The test database.</param>
    /// <param name="options">Hot-path options; the shipped defaults when omitted.</param>
    /// <returns>The stores.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is <see langword="null"/>.</exception>
    public static EdgeStores Open(TestDatabase database, FastPersistenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        return new EdgeStores(
            NpgsqlDataSource.Create(database.ConnectionString),
            options ?? new FastPersistenceOptions());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _readDataSource.DisposeAsync();
        await _dataSource.DisposeAsync();
    }
}
