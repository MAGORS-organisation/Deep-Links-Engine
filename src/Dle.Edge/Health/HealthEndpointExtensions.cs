using System.Text;

using Dle.Edge.Health;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the two probes an orchestrator needs (SHARED-KERNEL §15, NFR-05).
/// </summary>
/// <remarks>
/// The namespace is <c>Microsoft.AspNetCore.Builder</c>, matching every other module, so that the
/// composition root stays declarative.
/// </remarks>
public static class HealthEndpointExtensions
{
    /// <summary>Path of the liveness probe.</summary>
    public const string LivenessPath = "/healthz";

    /// <summary>Path of the readiness probe.</summary>
    public const string ReadinessPath = "/readyz";

    /// <summary>
    /// Maps <c>GET /healthz</c> and <c>GET /readyz</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The two probes answer different questions and must not be conflated. Liveness asks whether the
    /// process should be killed and restarted, so it runs no checks at all: a dependency being
    /// unreachable is never a reason to restart a stateless process, and wiring one into liveness is
    /// how a database blip becomes a restart loop across every replica at once.
    /// </para>
    /// <para>
    /// Readiness asks whether this instance should receive traffic, and its checks are chosen to say
    /// yes through exactly the failures §D.6 requires the edge to survive.
    /// </para>
    /// <para>
    /// Both paths are reserved slugs, so <see cref="SlugPolicy.Reserved"/> already keeps a link from
    /// shadowing them, and both opt out of rate limiting: a probe a limiter can answer with 429 is a
    /// probe that pulls an instance out of rotation during exactly the traffic spike it exists to
    /// survive.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteAsync,
        })
            .WithName("Liveness")
            .WithTags("health")
            .WithSummary("Liveness probe")
            .WithDescription("200 while the process is running. Runs no dependency checks by design.")
            .AllowAnonymous()
            .DisableRateLimiting();

        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains(ResolvePipelineHealthCheck.ReadyTag),
            ResponseWriter = WriteAsync,
        })
            .WithName("Readiness")
            .WithTags("health")
            .WithSummary("Readiness probe")
            .WithDescription(
                "200 while this instance can serve resolves, including when the geographic database is missing, " +
                "click events are being dropped or PostgreSQL is unreachable — all three report as degraded in " +
                "the body rather than removing the instance from rotation, because cached links keep resolving " +
                "through a database outage (§D.6, NFR-06).")
            .AllowAnonymous()
            .DisableRateLimiting();

        return app;
    }

    /// <summary>
    /// Writes the probe response.
    /// </summary>
    /// <remarks>
    /// Plain text, one line per check. A probe is read by an orchestrator and by a person with
    /// <c>curl</c>, neither of which benefits from a JSON envelope, and keeping it out of the JSON
    /// pipeline means the probe cannot fail for a reason unrelated to what it is reporting on.
    /// </remarks>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new StringBuilder(128);

        body.Append(report.Status.ToString()).Append('\n');

        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            body.Append(entry.Key)
                .Append(": ")
                .Append(entry.Value.Status.ToString())
                .Append('\n');
        }

        context.Response.ContentType = "text/plain; charset=utf-8";

        return context.Response.WriteAsync(body.ToString(), context.RequestAborted);
    }
}
