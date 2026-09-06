using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Dle.Control.Configuration;
using Dle.Edge.Configuration;

using Npgsql;

using Testcontainers.PostgreSql;

namespace Dle.IntegrationTests.Chaos;

/// <summary>
/// §D.6, row one: with PostgreSQL unavailable the edge keeps serving cached links, answers 503 for
/// the ones it has not cached, the control plane answers 503, and nothing anywhere answers 500.
/// </summary>
/// <remarks>
/// <para>
/// The distinction between 503 and 500 is not cosmetic. A 500 tells a load balancer the instance is
/// broken and should be taken out of rotation; a 503 tells it the instance is temporarily unable to
/// answer, which is the truth and is what every client retry policy is written against. A 500 also
/// risks putting the exception — connection strings, host names, stack frames — in front of an
/// unauthenticated caller.
/// </para>
/// <para>
/// This class starts a PostgreSQL of its own and takes it away. It deliberately does not touch the
/// assembly fixture's instance: every other test in the run shares that one, and a container that
/// failed to come back would turn one chaos test into a suite-wide failure.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The hosts started here are handed to DleIntegrationTest.DisposeWithTest, which "
                    + "disposes them at the end of the test. Disposing one inside the test would stop "
                    + "the very host the next assertion sends a request to.")]
public sealed class PostgresUnavailableTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private PostgreSqlContainer? _postgres;
    private TestDatabase? _chaosDatabase;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        if (!DockerRequirement.ShouldRun)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder(DleInfrastructureFixture.PostgresImage)
            .WithCleanUp(true)
            .Build();

        await _postgres.StartAsync(Ct);

        string connectionString = _postgres.GetConnectionString();

        await DleInfrastructureFixture.ApplyMigrationAsync(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
            Ct);

        _chaosDatabase = new TestDatabase("postgres", connectionString, connectionString, dropOnDispose: false);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_chaosDatabase is not null)
        {
            await _chaosDatabase.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>The database inside this class's own container.</summary>
    private TestDatabase Chaos =>
        _chaosDatabase ?? throw new InvalidOperationException("The chaos database was never created.");

    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task PostgresDown_ACachedLinkStillResolves_AndAnUncachedOneIs503NotFoundOr500()
    {
        string host = TestSeed.UniqueHost("chaos-pg");

        Guid tenantId = await TestSeed.TenantAsync(Chaos, "chaos-pg", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Chaos, tenantId, host, cancellationToken: Ct);

        _ = await TestSeed.LinkAsync(Chaos, tenantId, domainId, "warm", "https://example.test/warm", cancellationToken: Ct);
        _ = await TestSeed.LinkAsync(Chaos, tenantId, domainId, "cold", "https://example.test/cold", cancellationToken: Ct);

        DleTestHost<EdgeOptions> edge = StartEdgeAgainstChaosPostgres();
        using HttpClient client = edge.CreateDirectClient();

        // Warm one link into both cache levels.
        using (HttpResponseMessage warm = await GetAsync(client, host, "warm"))
        {
            Assert.Equal(HttpStatusCode.Found, warm.StatusCode);
        }

        await _postgres!.StopAsync(Ct);

        try
        {
            using HttpResponseMessage cached = await GetAsync(client, host, "warm");

            Assert.Equal(HttpStatusCode.Found, cached.StatusCode);
            Assert.Equal("https://example.test/warm", cached.Headers.Location?.ToString());

            using HttpResponseMessage uncached = await GetAsync(client, host, "cold");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, uncached.StatusCode);

            string body = await uncached.Content.ReadAsStringAsync(Ct);

            AssertNoInternals(body);
        }
        finally
        {
            await _postgres.StartAsync(Ct);
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task PostgresDown_TheControlPlaneAnswers503RatherThan500()
    {
        Guid tenantId = await TestSeed.TenantAsync(Chaos, "chaos-cp", cancellationToken: Ct);

        DleTestHost<DleControlOptions> control = StartControlAgainstChaosPostgres();

        ControlCredentials credentials = await ControlCredentials.IssueApiKeyAsync(
            control,
            Chaos,
            tenantId,
            "owner",
            Ct);

        using HttpClient client = control.CreateDirectClient();

        // One successful call first, so that the failure below is demonstrably the outage and not a
        // misconfiguration.
        using (HttpResponseMessage before = await credentials.GetAsync(client, "/api/v1/links", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await _postgres!.StopAsync(Ct);

        try
        {
            using HttpResponseMessage during = await credentials.GetAsync(client, "/api/v1/links", Ct);

            string body = await during.Content.ReadAsStringAsync(Ct);

            AssertNoInternals(body);

            Assert.True(
                during.StatusCode == HttpStatusCode.ServiceUnavailable,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§D.6 requires the control plane to answer 503 while PostgreSQL is unavailable, "
                    + $"and explicitly forbids a 500. It answered {(int)during.StatusCode}. Body:\n{body}"));
        }
        finally
        {
            await _postgres.StartAsync(Ct);
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "D.6")]
    public async Task PostgresDown_TheEdgeReadinessProbeReportsNotReady()
    {
        DleTestHost<EdgeOptions> edge = StartEdgeAgainstChaosPostgres();
        using HttpClient client = edge.CreateDirectClient();

        using (HttpResponseMessage ready = await client.GetAsync(new Uri("http://localhost/readyz"), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        await _postgres!.StopAsync(Ct);

        try
        {
            using HttpResponseMessage notReady = await client.GetAsync(new Uri("http://localhost/readyz"), Ct);

            // A readiness probe that keeps saying "ready" while the database is gone is how an
            // orchestrator keeps routing traffic into an instance that cannot serve it.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        }
        finally
        {
            await _postgres.StartAsync(Ct);
        }
    }

    /// <summary>Starts the edge against this class's own PostgreSQL and the shared Valkey.</summary>
    private DleTestHost<EdgeOptions> StartEdgeAgainstChaosPostgres()
    {
        Dictionary<string, string?> settings = DleTestSettings.Edge(
            Chaos.ConnectionString,
            Infrastructure.ValkeyConnectionString);

        DleTestHost<EdgeOptions> host = new(settings);
        DisposeWithTest(host);

        return host;
    }

    /// <summary>Starts the control plane against this class's own PostgreSQL.</summary>
    private DleTestHost<DleControlOptions> StartControlAgainstChaosPostgres()
    {
        Dictionary<string, string?> settings = DleTestSettings.Control(
            Chaos.ConnectionString,
            Infrastructure.ValkeyConnectionString);

        DleTestHost<DleControlOptions> host = new(settings);
        DisposeWithTest(host);

        return host;
    }

    /// <summary>Issues one resolve request.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string host, string slug)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}/{slug}")));

        request.Headers.UserAgent.ParseAdd(UserAgents.DesktopChrome);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Fails when a response body leaks anything about how the process is wired.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <remarks>
    /// §D.6 is explicit that no path answers 500 with internals in the body, and SHARED-KERNEL §17.5
    /// forbids logging the same values. A failure document says what the caller can do about it and
    /// nothing about the process that produced it.
    /// </remarks>
    private static void AssertNoInternals(string body)
    {
        string[] forbidden =
        [
            "Npgsql",
            "Host=",
            "Password",
            "Username=",
            "   at ",
            "StackTrace",
            "PostgresException",
            "System.",
        ];

        foreach (string fragment in forbidden)
        {
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
