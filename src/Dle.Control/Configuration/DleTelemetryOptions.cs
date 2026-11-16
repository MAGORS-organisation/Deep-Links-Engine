using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Observability configuration bound from <c>Dle:Telemetry</c>.
/// </summary>
public sealed class DleTelemetryOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:Telemetry";

    /// <summary>Service name reported to the tracing and metrics backend.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string ServiceName { get; set; } = "dle-control";

    /// <summary>OTLP endpoint. Empty means telemetry is collected but not exported.</summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Whether logs are written to the console.</summary>
    public bool ConsoleLogging { get; set; } = true;

    /// <summary>Fraction of traces sampled, from 0 to 1.</summary>
    [Range(0d, 1d)]
    public double TraceSampleRatio { get; set; } = 1d;

    /// <summary>Whether incoming HTTP requests are traced.</summary>
    public bool TraceRequests { get; set; } = true;

    /// <summary>
    /// Whether the native Prometheus <c>GET /metrics</c> scrape endpoint is enabled. Off by default:
    /// the endpoint is anonymous, and an ingress that forwards every path would publish it.
    /// </summary>
    public bool ExposePrometheusMetrics { get; set; }

    /// <summary>
    /// Bearer token required by <c>GET /metrics</c>. Empty allows unauthenticated scraping when
    /// <see cref="ExposePrometheusMetrics"/> is <see langword="true"/>, which is safe only when no
    /// ingress forwards <c>/metrics</c> to this service.
    /// </summary>
    public string? PrometheusScrapeToken { get; set; }
}
