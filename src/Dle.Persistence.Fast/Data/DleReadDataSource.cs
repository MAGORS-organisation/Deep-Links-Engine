namespace Dle.Persistence.Fast.Data;

/// <summary>
/// The data source the read-only hot path uses, which is a Postgres read replica when one is
/// configured and the primary otherwise (§B.8).
/// </summary>
/// <remarks>
/// <para>
/// Profile A of §B.8 is a single Postgres instance, profile B puts two read replicas behind the edge.
/// Both profiles run the same binary, so the difference has to be a configuration value rather than a
/// code path: supplying <c>ConnectionStrings:PostgresRead</c> moves link lookups and well-known
/// document generation off the primary, and omitting it leaves everything on one instance.
/// </para>
/// <para>
/// Only queries that tolerate replication lag are routed here. Reading a click back for attribution is
/// not one of them and deliberately uses the primary: a lagging replica would report "no match" for a
/// click that exists, and an attribution lost that way is never recovered.
/// </para>
/// <para>
/// When no replica is configured this type wraps the primary data source without owning it, so
/// disposal does not close the pool the rest of the application is still using.
/// </para>
/// </remarks>
public sealed class DleReadDataSource : IDisposable, IAsyncDisposable
{
    private readonly bool _owned;

    /// <summary>
    /// Creates the read data source.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source to read through.</param>
    /// <param name="isReplica">
    /// <see langword="true"/> when <paramref name="dataSource"/> is a dedicated replica pool that this
    /// instance owns and must dispose; <see langword="false"/> when it is the shared primary pool,
    /// which the container disposes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public DleReadDataSource(NpgsqlDataSource dataSource, bool isReplica)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        DataSource = dataSource;
        IsReplica = isReplica;
        _owned = isReplica;
    }

    /// <summary>The pool reads are issued against.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// Whether reads go to a dedicated replica. Exposed so health checks and diagnostics can report the
    /// deployment profile the process is actually running in, rather than the one it was meant to.
    /// </summary>
    public bool IsReplica { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_owned)
        {
            DataSource.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_owned)
        {
            await DataSource.DisposeAsync();
        }
    }
}
