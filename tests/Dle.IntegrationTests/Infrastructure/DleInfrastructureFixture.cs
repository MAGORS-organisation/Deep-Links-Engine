using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Dle.Persistence;

using DotNet.Testcontainers.Builders;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The one PostgreSQL 18 instance and the one Valkey 8 instance this assembly runs against (§D.4).
/// </summary>
/// <remarks>
/// <para>
/// Started once for the whole assembly, because starting a database per test class costs more than
/// every test in the class put together. Isolation is bought differently: the migration is applied
/// once to a template database, and each test that wants its own schema asks for a database created
/// from that template — which PostgreSQL does as a file copy, in milliseconds, and which leaves
/// every test with a schema that is byte for byte the one the migration produces.
/// </para>
/// <para>
/// This is also the first place the raw SQL of <c>InitialSchema</c> ever meets a live server: the
/// <c>dle_uuidv7()</c> shim, the partitioned <c>click_events</c> table, the pg_partman handover with
/// its fallback, and the autovacuum setting on <c>links</c>. If any of it is wrong,
/// <see cref="InitializeAsync"/> is where it shows, and <see cref="EnsureReady"/> reports it against
/// every test rather than hiding it behind a skip.
/// </para>
/// <para>
/// Nothing here throws when a container runtime is simply absent. That case is a skip, decided by
/// <see cref="RequiresDockerFactAttribute"/> before a test class is ever constructed; the fixture
/// records why it did nothing and stays inert. A runtime that <em>is</em> present and then fails is
/// the opposite: it is recorded and rethrown, because a migration that will not apply is the single
/// most important thing this suite can tell anyone.
/// </para>
/// </remarks>
public sealed class DleInfrastructureFixture : IAsyncLifetime
{
    /// <summary>PostgreSQL image. §D.4 names version 18; ADR-003 puts the floor at 16.</summary>
    public const string PostgresImage = "postgres:18-alpine";

    /// <summary>Valkey image. §D.4 names version 8.</summary>
    public const string ValkeyImage = "valkey/valkey:8-alpine";

    /// <summary>Port Valkey listens on inside the container.</summary>
    private const int ValkeyPort = 6379;

    /// <summary>Database the migration is applied to once and every test database is copied from.</summary>
    public const string TemplateDatabase = "dle_template";

    private static int _databaseCounter;

    private PostgreSqlContainer? _postgres;
    private RedisContainer? _valkey;

    /// <summary>Why the fixture did nothing, when it did nothing. Empty once it is ready.</summary>
    public string SkippedReason { get; private set; } = string.Empty;

    /// <summary>The exception that stopped the infrastructure from coming up, if one did.</summary>
    public Exception? StartupFailure { get; private set; }

    /// <summary>Whether the containers are up and the template schema is in place.</summary>
    public bool IsReady => _postgres is not null && StartupFailure is null && SkippedReason.Length == 0;

    /// <summary>
    /// Connection string for the container's maintenance database, used to create and drop the
    /// per-test databases.
    /// </summary>
    public string AdminConnectionString =>
        _postgres?.GetConnectionString() ?? throw NotStarted();

    /// <summary><c>host:port</c> of the Valkey instance, in the shape a Redis client expects.</summary>
    public string ValkeyConnectionString =>
        _valkey?.GetConnectionString() ?? throw NotStarted();

    /// <summary>
    /// An endpoint on the Valkey host where nothing is listening, for the chaos case of §D.6.
    /// </summary>
    /// <remarks>
    /// The address resolves and the port refuses, which is what "Valkey is down" looks like to a
    /// client — as opposed to a hostname that does not resolve, which fails differently and earlier.
    /// </remarks>
    public string UnreachableValkeyConnectionString =>
        string.Create(CultureInfo.InvariantCulture, $"{Host(ValkeyConnectionString)}:1,abortConnect=false,connectTimeout=250,connectRetry=1,syncTimeout=250");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerRequirement.ShouldRun)
        {
            SkippedReason = DockerRequirement.SkipReason;
            return;
        }

        try
        {
            _postgres = new PostgreSqlBuilder(PostgresImage)
                .WithCleanUp(true)
                .Build();

            _valkey = new RedisBuilder(ValkeyImage)

                // The default Redis wait strategy shells out to redis-cli, which a Valkey image is
                // not obliged to carry. Waiting for the port is equivalent here and image agnostic.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ValkeyPort))
                .WithCleanUp(true)
                .Build();

            await Task.WhenAll(_postgres.StartAsync(), _valkey.StartAsync());

            await ApplyMigrationAsync(TemplateConnectionString());
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // Recorded rather than thrown, so that every test reports the same, complete diagnosis
        // through EnsureReady instead of xUnit reporting one opaque fixture error. It is never
        // swallowed: EnsureReady rethrows it as the inner exception of a message that says where it
        // came from.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            StartupFailure = exception;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Pools are cleared first: a pooled connection to a database inside a container that is
        // going away is a socket error in the logs of whatever runs next.
        NpgsqlConnection.ClearAllPools();

        if (_valkey is not null)
        {
            await _valkey.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Fails the calling test when the infrastructure is not usable, with the reason it is not.
    /// </summary>
    /// <exception cref="InvalidOperationException">The containers never came up.</exception>
    /// <remarks>
    /// A test that reaches this without a container runtime has been forced to run by
    /// <c>DLE_TESTS_REQUIRE_DOCKER</c>, and the only correct outcome then is a failure that says so.
    /// </remarks>
    public void EnsureReady()
    {
        if (StartupFailure is not null)
        {
            throw new InvalidOperationException(
                "The integration infrastructure failed to start. This is a real failure, not a "
                + "missing dependency: a container runtime answered and then something went wrong "
                + "— most often the InitialSchema migration itself. See the inner exception.",
                StartupFailure);
        }

        if (SkippedReason.Length > 0)
        {
            throw new InvalidOperationException(SkippedReason);
        }

        if (_postgres is null || _valkey is null)
        {
            throw NotStarted();
        }
    }

    /// <summary>
    /// Creates a database of this test's own, copied from the migrated template.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new database, which drops itself when disposed.</returns>
    /// <remarks>
    /// <c>CREATE DATABASE … TEMPLATE</c> rather than re-running the migration: the copy is a few
    /// milliseconds against a few seconds, and — more to the point — every test then runs against
    /// the schema the migration actually produced, not against one a helper re-created by hand.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "CREATE DATABASE takes no parameters. The name is generated by the counter "
                        + "in this class and never comes from outside it.")]
    public async Task<TestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"dle_t{Interlocked.Increment(ref _databaseCounter)}");

        await using (NpgsqlConnection admin = new(AdminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);

            await using NpgsqlCommand create = admin.CreateCommand();
            create.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");

            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return new TestDatabase(name, DatabaseConnectionString(name), AdminConnectionString);
    }

    /// <summary>Connection string for a named database on the container.</summary>
    /// <param name="database">The database name.</param>
    /// <returns>The connection string.</returns>
    public string DatabaseConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Connection string used to apply the migration to the template.
    /// </summary>
    /// <remarks>
    /// Pooling is off. <c>CREATE DATABASE … TEMPLATE</c> refuses while any session is connected to
    /// the template, and a pooled connection outlives the code that opened it.
    /// </remarks>
    private string TemplateConnectionString() =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = TemplateDatabase,
            Pooling = false,
        }.ConnectionString;

    /// <summary>
    /// Applies <c>InitialSchema</c> through the production composition, not through a hand rolled
    /// script runner.
    /// </summary>
    /// <param name="connectionString">Where to apply it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the schema is in place.</returns>
    /// <remarks>
    /// <see cref="DlePersistenceServiceCollectionExtensions.AddDlePersistence"/> is what a deployment
    /// calls, so it is what the migration is exercised through: the same migrations assembly, the
    /// same history table, the same command timeout. EF Core creates the database when it is absent,
    /// which is how the template comes into existence.
    /// </remarks>
    public static async Task ApplyMigrationAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Postgres"] = connectionString,
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDlePersistence(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();

        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The host part of a <c>host:port</c> endpoint.</summary>
    private static string Host(string endpoint)
    {
        int separator = endpoint.LastIndexOf(':');

        return separator < 0 ? endpoint : endpoint[..separator];
    }

    private static InvalidOperationException NotStarted() =>
        new("The integration infrastructure has not been started.");
}
