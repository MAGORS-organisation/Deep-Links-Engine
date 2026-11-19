using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Dle.Control.Telemetry;

/// <summary>
/// The activity source every control-plane span hangs off.
/// </summary>
/// <remarks>
/// One source per deployment unit, named after it. Trace context arrives and leaves through the W3C
/// <c>traceparent</c> header, so an SDK span and the server span it caused belong to one trace
/// without either side knowing about the other.
/// </remarks>
public static class ControlActivitySource
{
    /// <summary>Name of the source, as it appears in the tracing backend.</summary>
    public const string Name = "Dle.Control";

    /// <summary>The source itself.</summary>
    public static ActivitySource Source { get; } = new(Name, AssemblyVersion.Value);

    /// <summary>Starts a span for one control-plane operation.</summary>
    /// <param name="name">Operation name, for example <c>link.create</c>.</param>
    /// <returns>The activity, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? Start(string name) => Source.StartActivity(name, ActivityKind.Internal);
}

/// <summary>
/// The control-plane instrument set.
/// </summary>
/// <remarks>
/// The metric names carry the <c>dle_</c> prefix. They are deliberately few: the control
/// plane is not the latency-critical half of the product, and an instrument that nobody alerts on is
/// cardinality somebody pays for.
/// </remarks>
public sealed class ControlMetrics : IDisposable
{
    /// <summary>Name of the meter, which the exporter subscribes to.</summary>
    public const string MeterName = "Dle.Control";

    private readonly Meter _meter;
    private readonly Counter<long> _linksCreated;
    private readonly Counter<long> _linksRejected;
    private readonly Counter<long> _domainVerifications;
    private readonly Histogram<double> _bulkBatchRows;

    /// <summary>Creates the instrument set.</summary>
    /// <param name="meterFactory">Meter factory, or <see langword="null"/> outside a host.</param>
    public ControlMetrics(IMeterFactory? meterFactory)
    {
        // The factory's meter is owned by the factory and disposed with the container; the fallback
        // is only for a test or a tool that has no host, and is owned here. Disposing either twice
        // is harmless, and disposing neither leaks a meter for the life of the process.
        Meter meter = meterFactory?.Create(MeterName, AssemblyVersion.Value)
            ?? new Meter(MeterName, AssemblyVersion.Value);

        _meter = meter;

        _linksCreated = meter.CreateCounter<long>(
            "dle.links.created",
            unit: "{link}",
            description: "Links created through the control plane.");

        _linksRejected = meter.CreateCounter<long>(
            "dle.links.rejected",
            unit: "{link}",
            description: "Link writes refused, tagged with the reason.");

        _domainVerifications = meter.CreateCounter<long>(
            "dle.domain.verifications",
            unit: "{run}",
            description: "Association file verification runs, tagged with the outcome.");

        _bulkBatchRows = meter.CreateHistogram<double>(
            "dle.links.bulk.rows",
            unit: "{row}",
            description: "Rows per streamed bulk import batch.");
    }

    /// <summary>Records a created link.</summary>
    public void LinkCreated() => _linksCreated.Add(1);

    /// <summary>Records a refused link write.</summary>
    /// <param name="reason">Why it was refused, from the write error names.</param>
    public void LinkRejected(string reason) =>
        _linksRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records a verification run.</summary>
    /// <param name="outcome"><c>ok</c>, <c>warning</c> or <c>failed</c>.</param>
    public void DomainVerified(string outcome) =>
        _domainVerifications.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records the size of a finished bulk batch.</summary>
    /// <param name="rows">How many rows the batch carried.</param>
    public void BulkBatch(int rows) => _bulkBatchRows.Record(rows);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// The product version, reported as the service version and the instrument version.
/// </summary>
/// <remarks>
/// The release build passes the tag as <c>-p:Version</c>, which reaches only the informational
/// version: the numeric assembly version cannot carry a pre-release label and would report
/// <c>v1.0.0-rc.1</c> as <c>1.0.0.0</c>.
/// </remarks>
internal static class AssemblyVersion
{
    /// <summary>Informational version of the control assembly, without build metadata.</summary>
    internal static string Value { get; } = Read(typeof(AssemblyVersion).Assembly);

    /// <summary>Reads the product version of an assembly.</summary>
    /// <param name="assembly">The assembly to read.</param>
    /// <returns>
    /// The informational version without its <c>+build</c> suffix (the commit a local build appends),
    /// else the three-part assembly version, else <c>0.0.0</c>.
    /// </returns>
    internal static string Read(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int metadata = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        string? version = metadata >= 0 ? informational![..metadata] : informational;
        return string.IsNullOrWhiteSpace(version)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : version;
    }
}
