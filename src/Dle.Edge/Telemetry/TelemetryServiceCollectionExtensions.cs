using Dle.Edge.Telemetry;
using Dle.Persistence.Fast.Telemetry;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the observability module (SHARED-KERNEL §15, NFR-12, §C.6).
/// </summary>
/// <remarks>
/// The namespace is <c>Microsoft.Extensions.DependencyInjection</c>, matching every other module, so
/// that the composition root stays declarative.
/// </remarks>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the edge instrument set and the OpenTelemetry traces, metrics and logs pipelines.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration; reads <c>Dle:Telemetry</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Both meters are registered with the metrics pipeline: the edge's own and the one the bounded
    /// click channel publishes from <c>Dle.Persistence.Fast</c>. Without the second one
    /// <c>dle_click_events_dropped_total</c> would exist but never be exported, and §C.6 puts an
    /// alert on precisely that counter.
    /// </para>
    /// <para>
    /// Trace context arrives and leaves through the W3C <c>traceparent</c> header, which is what
    /// joins an SDK span to the server span it caused (NFR-12). ASP.NET Core extracts it before the
    /// endpoint runs, so the <c>resolve</c> span and everything under it is already a child of the
    /// caller's trace; the sampler is parent based so that a trace the SDK decided to record is not
    /// then dropped here.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDleTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(EdgeTelemetryOptions.SectionName);

        services.AddOptions<EdgeTelemetryOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The pipeline has to be shaped now, before the container is built, so the values are read
        // once here as well. Validation above is what guarantees the process refuses to start on a
        // malformed value rather than silently exporting nowhere.
        EdgeTelemetryOptions options = section.Get<EdgeTelemetryOptions>() ?? new EdgeTelemetryOptions();

        services.AddMetrics();
        services.TryAddSingleton<DomainVerificationFailureRegistry>();
        services.TryAddSingleton(static sp => new EdgeMetrics(
            sp.GetService<System.Diagnostics.Metrics.IMeterFactory>(),
            sp.GetRequiredService<DomainVerificationFailureRegistry>()));

        string? otlpEndpoint = string.IsNullOrWhiteSpace(options.OtlpEndpoint) ? null : options.OtlpEndpoint.Trim();

        _ = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceNamespace: options.ServiceNamespace,
                serviceVersion: ThisAssembly.Version,
                autoGenerateServiceInstanceId: true))
            .WithTracing(tracing => ConfigureTracing(tracing, options, otlpEndpoint))
            .WithMetrics(metrics => ConfigureMetrics(metrics, options, otlpEndpoint))
            .WithLogging(
                logging => ConfigureLogging(logging, otlpEndpoint),
                logger =>
                {
                    logger.IncludeFormattedMessage = true;
                    logger.IncludeScopes = true;
                });

        return services;
    }

    private static void ConfigureTracing(TracerProviderBuilder tracing, EdgeTelemetryOptions options, string? otlpEndpoint)
    {
        _ = tracing.AddSource(EdgeActivitySource.Name);

        // Parent based: a trace the SDK already decided to record stays recorded end to end, and only
        // traces this process starts are subject to the ratio.
        _ = tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)));

        if (options.InstrumentHttp)
        {
            _ = tracing.AddAspNetCoreInstrumentation(instrumentation =>
            {
                // The recorded URL would otherwise carry the full query string, and SHARED-KERNEL
                // §17.5 rules that out. Health probes are filtered because they would otherwise be
                // most of the sampled traces on an idle instance.
                instrumentation.RecordException = true;
                instrumentation.Filter = static context => !IsProbe(context.Request.Path);
            });

            _ = tracing.AddHttpClientInstrumentation();
        }

        if (otlpEndpoint is not null)
        {
            _ = tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    private static void ConfigureMetrics(MeterProviderBuilder metrics, EdgeTelemetryOptions options, string? otlpEndpoint)
    {
        _ = metrics.AddMeter(EdgeMetrics.MeterName);
        _ = metrics.AddMeter(FastPersistenceMetrics.MeterName);

        // Buckets are the latency budget of NFR-01 written down: p50 ≤ 8 ms, p95 ≤ 25 ms,
        // p99 ≤ 50 ms on a cache hit and p99 ≤ 120 ms on a miss (NFR-02). The default OpenTelemetry
        // boundaries start at 5 ms and jump to 10, which puts the entire service-level objective into
        // two buckets and makes the p95 unmeasurable.
        _ = metrics.AddView(
            "dle.resolve.duration",
            new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.001d, 0.002d, 0.004d, 0.008d, 0.015d, 0.025d, 0.05d, 0.1d, 0.25d, 0.5d, 1d],
            });

        if (options.InstrumentHttp)
        {
            _ = metrics.AddAspNetCoreInstrumentation();
            _ = metrics.AddHttpClientInstrumentation();
        }

        if (options.InstrumentRuntime)
        {
            _ = metrics.AddRuntimeInstrumentation();
        }

        if (otlpEndpoint is not null)
        {
            _ = metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    private static void ConfigureLogging(LoggerProviderBuilder logging, string? otlpEndpoint)
    {
        if (otlpEndpoint is not null)
        {
            _ = logging.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/livez", StringComparison.OrdinalIgnoreCase);
}
