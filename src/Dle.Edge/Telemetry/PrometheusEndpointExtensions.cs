using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Telemetry;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the native Prometheus <c>GET /metrics</c> scrape endpoint on the edge data plane.
/// </summary>
public static class PrometheusEndpointExtensions
{
    /// <summary>Path of the Prometheus metrics endpoint.</summary>
    public const string MetricsPath = "/metrics";

    /// <summary>
    /// Maps <c>GET /metrics</c> when <see cref="EdgeTelemetryOptions.ExposePrometheusMetrics"/> is
    /// enabled.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Touch the singleton collector at route registration so its MeterListener captures
        // startup gauges before the first scrape arrives.
        _ = app.ServiceProvider.GetService<PrometheusMetricsCollector>();

        app.MapGet(MetricsPath, static IResult (
            HttpContext context,
            PrometheusMetricsCollector collector,
            IOptions<EdgeTelemetryOptions> options) =>
        {
            EdgeTelemetryOptions telemetry = options.Value;
            if (!telemetry.ExposePrometheusMetrics)
            {
                return TypedResults.NotFound();
            }

            if (!PrometheusMetricsCollector.IsAuthorizedScrape(
                telemetry.PrometheusScrapeToken,
                context.Request.Headers.Authorization))
            {
                return TypedResults.Unauthorized();
            }

            return TypedResults.Text(collector.Scrape(), PrometheusMetricsCollector.ContentType);
        })
            .WithName("EdgePrometheusMetrics")
            .WithTags("health")
            .AllowAnonymous()
            .DisableRateLimiting()
            .ExcludeFromDescription();

        return app;
    }
}
