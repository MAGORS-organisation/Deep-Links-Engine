using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Observability configuration bound from <c>Dle:Telemetry</c> (§C.6).
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
}
