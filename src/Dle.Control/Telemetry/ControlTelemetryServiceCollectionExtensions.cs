using System.Diagnostics.Metrics;

using Dle.Control.Configuration;
using Dle.Control.Telemetry;

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
/// The single composition entry point of the control-plane observability module
/// (SHARED-KERNEL §15, §C.6, NFR-12).
/// </summary>
public static class ControlTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the control-plane instrument set and the OpenTelemetry traces, metrics and logs
    /// pipelines.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for <c>Dle:Telemetry</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The sampler is parent based, so a trace the SDK or the edge already decided to record stays
    /// recorded end to end and only traces this process starts are subject to the ratio.
    /// </para>
    /// <para>
    /// ASP.NET Core instrumentation is configured to drop the health probes and never to record a
    /// full query string. Prohibition 5 forbids logging the <c>Authorization</c> header, a full
    /// referrer, a full query string or a raw address at information level or below, and a trace
    /// attribute is a log line with a different name.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDleTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(DleTelemetryOptions.SectionName);

        services.AddOptions<DleTelemetryOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The pipeline is shaped before the container is built, so the values are read once here as
        // well. The validation above is what makes a malformed value a refusal to start rather than
        // an exporter that quietly sends nowhere.
        DleTelemetryOptions options = section.Get<DleTelemetryOptions>() ?? new DleTelemetryOptions();

        services.AddMetrics();
        services.TryAddSingleton(static sp => new ControlMetrics(sp.GetService<IMeterFactory>()));

        string? otlpEndpoint = string.IsNullOrWhiteSpace(options.OtlpEndpoint)
            ? null
            : options.OtlpEndpoint.Trim();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: AssemblyVersion.Value,
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

    /// <summary>Shapes the trace pipeline.</summary>
    /// <param name="tracing">The builder.</param>
    /// <param name="options">Telemetry options.</param>
    /// <param name="otlpEndpoint">Export endpoint, or <see langword="null"/> to collect only.</param>
    private static void ConfigureTracing(
        TracerProviderBuilder tracing,
        DleTelemetryOptions options,
        string? otlpEndpoint)
    {
        tracing.AddSource(ControlActivitySource.Name);

        tracing.SetSampler(new ParentBasedSampler(
            new TraceIdRatioBasedSampler(options.TraceSampleRatio)));

        if (options.TraceRequests)
        {
            tracing.AddAspNetCoreInstrumentation(instrumentation =>
            {
                instrumentation.RecordException = true;
                instrumentation.Filter = static context => !IsProbe(context.Request.Path);
            });

            tracing.AddHttpClientInstrumentation();
        }

        if (otlpEndpoint is not null)
        {
            tracing.AddOtlpExporter(exporter =>
                exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    /// <summary>Shapes the metrics pipeline.</summary>
    /// <param name="metrics">The builder.</param>
    /// <param name="options">Telemetry options.</param>
    /// <param name="otlpEndpoint">Export endpoint, or <see langword="null"/> to collect only.</param>
    private static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        DleTelemetryOptions options,
        string? otlpEndpoint)
    {
        metrics.AddMeter(ControlMetrics.MeterName);

        if (options.TraceRequests)
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();
        }

        metrics.AddRuntimeInstrumentation();

        if (otlpEndpoint is not null)
        {
            metrics.AddOtlpExporter(exporter =>
                exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    /// <summary>Shapes the log pipeline.</summary>
    /// <param name="logging">The builder.</param>
    /// <param name="otlpEndpoint">Export endpoint, or <see langword="null"/> to collect only.</param>
    private static void ConfigureLogging(LoggerProviderBuilder logging, string? otlpEndpoint)
    {
        if (otlpEndpoint is not null)
        {
            logging.AddOtlpExporter(exporter =>
                exporter.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
        }
    }

    /// <summary>Whether a path is one of the health probes.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> for a probe.</returns>
    /// <remarks>
    /// Probes are filtered because they would otherwise be most of the sampled traces on an idle
    /// instance, and a trace list full of liveness checks is a trace list nobody reads.
    /// </remarks>
    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/livez", StringComparison.OrdinalIgnoreCase);
}
