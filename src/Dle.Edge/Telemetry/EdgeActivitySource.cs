using System.Diagnostics;
using System.Reflection;

namespace Dle.Edge.Telemetry;

/// <summary>
/// The tracing vocabulary of the resolve path: one <c>resolve</c> span with <c>lookup</c>,
/// <c>classify</c>, <c>route</c> and <c>render</c> children.
/// </summary>
/// <remarks>
/// <para>
/// The span names are constants rather than literals at the call sites because they are the join
/// between the edge and the dashboards, and because the SDK correlates its own spans with these
/// through the <c>traceparent</c> header. Renaming one in a single place is then a rename,
/// not a silent dashboard outage.
/// </para>
/// <para>
/// Every <c>Start…</c> method returns a nullable <see cref="Activity"/>. With no listener attached —
/// the common case, since only a fraction of traces is sampled — <c>StartActivity</c> returns
/// <see langword="null"/> and costs nothing, which is the property that lets the hot path be
/// instrumented at all.
/// </para>
/// </remarks>
public static class EdgeActivitySource
{
    /// <summary>Name of the activity source, registered with the tracer provider.</summary>
    public const string Name = "Dle.Edge";

    /// <summary>Name of the span covering one whole resolve.</summary>
    public const string ResolveSpan = "resolve";

    /// <summary>Name of the child span covering the cache and database lookup.</summary>
    public const string LookupSpan = "lookup";

    /// <summary>Name of the child span covering client classification.</summary>
    public const string ClassifySpan = "classify";

    /// <summary>Name of the child span covering rule evaluation.</summary>
    public const string RouteSpan = "route";

    /// <summary>Name of the child span covering response generation.</summary>
    public const string RenderSpan = "render";

    private static readonly ActivitySource Source = new(Name, ThisAssembly.Version);

    /// <summary>Starts the span covering one resolve.</summary>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? StartResolve() => Source.StartActivity(ResolveSpan, ActivityKind.Internal);

    /// <summary>Starts the cache and database lookup span.</summary>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? StartLookup() => Source.StartActivity(LookupSpan, ActivityKind.Internal);

    /// <summary>Starts the client classification span.</summary>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? StartClassify() => Source.StartActivity(ClassifySpan, ActivityKind.Internal);

    /// <summary>Starts the rule evaluation span.</summary>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? StartRoute() => Source.StartActivity(RouteSpan, ActivityKind.Internal);

    /// <summary>Starts the response generation span.</summary>
    /// <returns>The span, or <see langword="null"/> when nothing is listening.</returns>
    public static Activity? StartRender() => Source.StartActivity(RenderSpan, ActivityKind.Internal);
}

/// <summary>
/// The product version, used as the activity source version and the service version.
/// </summary>
/// <remarks>
/// Read once from the assembly rather than hard coded, so a released build reports the version it
/// actually is. The release build passes the tag as <c>-p:Version</c>, which reaches only the
/// informational version: the numeric assembly version cannot carry a pre-release label and would
/// report <c>v1.0.0-rc.1</c> as <c>1.0.0.0</c>.
/// </remarks>
internal static class ThisAssembly
{
    /// <summary>Informational version of the edge assembly, without build metadata.</summary>
    internal static string Version { get; } = Read(typeof(ThisAssembly).Assembly);

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
