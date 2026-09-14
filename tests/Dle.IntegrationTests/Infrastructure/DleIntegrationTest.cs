using Dle.Control.Configuration;
using Dle.Edge.Configuration;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for every test in this assembly: one database of its own, and the two hosts on top of it.
/// </summary>
/// <remarks>
/// <para>
/// The database is created in <see cref="InitializeAsync"/> and dropped in
/// <see cref="DisposeAsync"/>, so no test can see another's rows. The hosts are created on demand,
/// because most tests need only one of them and starting a host costs more than the query it is
/// there to make.
/// </para>
/// <para>
/// When there is no container runtime this class does nothing at all: every test method is decorated
/// with <see cref="RequiresDockerFactAttribute"/> and is skipped before it runs, so the fields stay
/// unset and no container is ever asked for.
/// </para>
/// </remarks>
public abstract class DleIntegrationTest : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private TestDatabase? _database;

    /// <summary>Creates the test.</summary>
    /// <param name="infrastructure">The assembly-wide containers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    protected DleIntegrationTest(DleInfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);

        Infrastructure = infrastructure;
    }

    /// <summary>The assembly-wide PostgreSQL and Valkey instances.</summary>
    protected DleInfrastructureFixture Infrastructure { get; }

    /// <summary>This test's own database.</summary>
    protected TestDatabase Database =>
        _database ?? throw new InvalidOperationException(
            "The test database was never created, which means the infrastructure was not available. "
            + "A test that reaches this should have been skipped by RequiresDockerFactAttribute.");

    /// <summary>The token xUnit cancels when the test is abandoned.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync()
    {
        if (!DockerRequirement.ShouldRun)
        {
            // Skipped by the attribute; nothing to prepare and nothing to report.
            return;
        }

        Infrastructure.EnsureReady();

        _database = await Infrastructure.CreateDatabaseAsync(Ct);
        _disposables.Add(_database);
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        // Reverse order: the hosts hold connection pools against the database that is dropped last.
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        _disposables.Clear();
        _database = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Starts the edge data plane against this test's database.
    /// </summary>
    /// <param name="configure">Optional adjustment of the configuration before the host is built.</param>
    /// <param name="valkey">
    /// The L2 connection string. Defaults to the fixture's Valkey; pass
    /// <see cref="DleInfrastructureFixture.UnreachableValkeyConnectionString"/> for the §D.6 case, or
    /// an empty string for an L1-only host (§B.8 profile A).
    /// </param>
    /// <returns>The host, disposed with the test.</returns>
    protected DleTestHost<EdgeOptions> StartEdge(
        Action<Dictionary<string, string?>>? configure = null,
        string? valkey = null)
    {
        Dictionary<string, string?> settings = DleTestSettings.Edge(
            Database.ConnectionString,
            valkey ?? Infrastructure.ValkeyConnectionString);

        configure?.Invoke(settings);

        DleTestHost<EdgeOptions> host = new(settings);
        _disposables.Add(host);

        return host;
    }

    /// <summary>
    /// Starts the control plane against this test's database.
    /// </summary>
    /// <param name="configure">Optional adjustment of the configuration before the host is built.</param>
    /// <param name="valkey">The L2 connection string; defaults to the fixture's Valkey.</param>
    /// <returns>The host, disposed with the test.</returns>
    protected DleTestHost<DleControlOptions> StartControl(
        Action<Dictionary<string, string?>>? configure = null,
        string? valkey = null)
    {
        Dictionary<string, string?> settings = DleTestSettings.Control(
            Database.ConnectionString,
            valkey ?? Infrastructure.ValkeyConnectionString);

        configure?.Invoke(settings);

        DleTestHost<DleControlOptions> host = new(settings);
        _disposables.Add(host);

        return host;
    }

    /// <summary>Registers something for disposal at the end of the test.</summary>
    /// <param name="disposable">The resource.</param>
    protected void DisposeWithTest(IAsyncDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);

        _disposables.Add(disposable);
    }
}
