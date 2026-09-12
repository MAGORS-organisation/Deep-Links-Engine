using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Dle.Edge.Health;

/// <summary>
/// Reports whether the edge can currently reach PostgreSQL, as a <em>degraded</em> rather than an
/// unhealthy condition.
/// </summary>
/// <remarks>
/// <para>
/// §D.6 and NFR-06 are explicit about what a PostgreSQL outage means for the edge: cached links keep
/// resolving from L1 and L2, uncached ones answer 503. An instance in that state is still the best
/// thing the load balancer has — every instance is in the same state — so the probe must not take
/// it out of rotation. Degraded keeps the probe at 200 and puts the fact in the body, where an
/// operator reading <c>/readyz</c> and a dashboard scraping it both see it.
/// </para>
/// <para>
/// The check is bounded by a short budget of its own. A probe that hangs on a wedged database is
/// indistinguishable, to an orchestrator, from an instance that has stopped, and the orchestrator
/// would then do the one thing this check exists to prevent.
/// </para>
/// </remarks>
public sealed class DatabaseReachabilityHealthCheck : IHealthCheck
{
    /// <summary>Name under which the check is registered and reported.</summary>
    public const string CheckName = "database";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the check.</summary>
    /// <param name="dataSource">The primary connection pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public DatabaseReachabilityHealthCheck(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(budget.Token);
            await using NpgsqlCommand probe = connection.CreateCommand();
            probe.CommandText = "SELECT 1";
            _ = await probe.ExecuteScalarAsync(budget.Token);

            return HealthCheckResult.Healthy("PostgreSQL answers.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Degraded("PostgreSQL did not answer within the probe budget", exception: null);
        }
        catch (NpgsqlException exception)
        {
            // The reason is logged through the health check's own exception member, which the probe
            // response deliberately does not echo: a probe is reachable without a credential.
            return Degraded("PostgreSQL is unreachable", exception);
        }
    }

    private static HealthCheckResult Degraded(string reason, Exception? exception) =>
        HealthCheckResult.Degraded(
            reason + "; cached links keep resolving from L1 and L2, uncached links answer 503 (§D.6).",
            exception);
}
