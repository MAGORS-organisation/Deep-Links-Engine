using System.Text.Json;

using Dle.Control.Features.Health;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the health probes (SHARED-KERNEL §15, §D.6).
/// </summary>
/// <remarks>
/// <para>
/// Liveness and readiness answer two different questions and must not be the same endpoint.
/// <c>/healthz</c> asks whether the process is alive: if it fails, the orchestrator restarts the
/// container. <c>/readyz</c> asks whether it can serve: if it fails, the orchestrator stops sending
/// traffic. Wiring a database check into liveness is the classic mistake — a database blip then
/// restarts every replica at once, turning a recoverable dependency failure into an outage.
/// </para>
/// <para>
/// Both are anonymous, because a probe cannot hold a credential, and both are excluded from tracing
/// and from the rate limiter: throttling a liveness probe would turn a busy minute into a restart.
/// </para>
/// </remarks>
public static class HealthEndpointExtensions
{
    /// <summary>Tag marking the checks that gate readiness.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Maps <c>/healthz</c>, <c>/livez</c> and <c>/readyz</c>.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteAsync,
        })
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithName("Liveness")
            .WithTags("Health")
            .ExcludeFromDescription();

        app.MapHealthChecks("/livez", new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteAsync,
        })
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithName("LivenessAlias")
            .WithTags("Health")
            .ExcludeFromDescription();

        app.MapHealthChecks("/readyz", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
        })
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithName("Readiness")
            .WithTags("Health")
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>
    /// Writes the probe response.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="report">The health report.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <remarks>
    /// Check names and their status, and nothing else. A probe response is reachable without a
    /// credential, so the exception text and the connection detail a health check may carry stay
    /// out of it: those belong in the logs, where they are already correlated with the trace.
    /// </remarks>
    private static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        Dictionary<string, string> checks = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            checks[entry.Key] = entry.Value.Status.ToString();
        }

        HealthDocument document = new()
        {
            Status = report.Status.ToString(),
            DurationMs = (long)report.TotalDuration.TotalMilliseconds,
            Checks = checks,
        };

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            document,
            HealthJsonContext.Default.HealthDocument,
            context.RequestAborted);
    }
}
