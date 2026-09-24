using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace Dle.Control.Features.Health;

/// <summary>
/// The readiness check: can this process reach its database (§D.6).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the cheapest question that is still meaningful. <c>CanConnectAsync</c> opens a
/// connection from the pool and does nothing else; a check that ran a real query would measure the
/// database's load as well as its reachability, and a readiness probe that fails under load takes
/// replicas out of rotation exactly when they are needed.
/// </para>
/// <para>
/// It gates readiness, never liveness. A database blip must stop traffic being routed here; it must
/// not restart the process, which would achieve nothing and would lose the connection pool.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly DleDbContext _db;

    /// <summary>Creates the check.</summary>
    /// <param name="db">The control-plane context.</param>
    public DatabaseHealthCheck(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            bool reachable = await _db.Database.CanConnectAsync(cancellationToken);

            return reachable
                ? HealthCheckResult.Healthy("The control-plane database is reachable.")
                : HealthCheckResult.Unhealthy("The control-plane database did not accept a connection.");
        }
        catch (NpgsqlException exception)
        {
            // The reason is logged through the health check's own exception member, which the probe
            // response deliberately does not echo: a probe is reachable without a credential.
            return HealthCheckResult.Unhealthy(
                "The control-plane database is unreachable.",
                exception);
        }
    }
}

/// <summary>The body of a health probe response.</summary>
public sealed record HealthDocument
{
    /// <summary>Overall status: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</summary>
    public required string Status { get; init; }

    /// <summary>How long the checks took, in milliseconds.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Status per registered check.</summary>
    public IReadOnlyDictionary<string, string> Checks { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Source-generated serialization metadata for the probe response.
/// </summary>
/// <remarks>
/// A probe is called every few seconds for the life of the process, which makes it one of the few
/// control-plane responses worth keeping off the reflection-based serializer (SHARED-KERNEL §17.3).
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthDocument))]
public sealed partial class HealthJsonContext : JsonSerializerContext;
