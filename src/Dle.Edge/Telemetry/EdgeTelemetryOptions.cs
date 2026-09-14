using System.ComponentModel.DataAnnotations;

namespace Dle.Edge.Telemetry;

/// <summary>
/// OpenTelemetry wiring, bound from <c>Dle:Telemetry</c> (NFR-12, §C.6).
/// </summary>
/// <remarks>
/// With no OTLP endpoint configured the pipeline is still built and the instruments still record;
/// nothing is exported. That is intentional: a single-container evaluation deployment (NFR-09) has
/// nowhere to export to, and metrics that only exist when a collector happens to be configured are
/// metrics nobody writes an alert against.
/// </remarks>
public sealed class EdgeTelemetryOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Telemetry";

    /// <summary>Value of <c>service.name</c> on every exported signal.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ServiceName { get; set; } = "dle-edge";

    /// <summary>Value of <c>service.namespace</c> on every exported signal.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ServiceNamespace { get; set; } = "dle";

    /// <summary>
    /// OTLP collector endpoint. Empty disables export and leaves the instruments in place.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Fraction of traces sampled, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// The sampler is parent-based, so a trace started by an SDK and propagated through
    /// <c>traceparent</c> keeps the decision the SDK made and stays joined end to end (NFR-12). This
    /// ratio only applies to traces the edge starts itself.
    /// </remarks>
    [Range(0d, 1d)]
    public double TraceSampleRatio { get; set; } = 0.05d;

    /// <summary>Whether ASP.NET Core and HTTP client instrumentation are added.</summary>
    public bool InstrumentHttp { get; set; } = true;

    /// <summary>Whether .NET runtime instrumentation is added.</summary>
    public bool InstrumentRuntime { get; set; } = true;
}
