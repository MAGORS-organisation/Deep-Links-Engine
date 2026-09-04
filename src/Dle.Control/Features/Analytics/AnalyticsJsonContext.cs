using System.Text.Json.Serialization;

using Dle.Domain.Analytics;
using Dle.Domain.Contracts;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// Source generated serialization metadata for the reporting responses.
/// </summary>
/// <remarks>
/// <para>
/// Reporting is not the resolve path, so SHARED-KERNEL §17.3 does not strictly reach it. It is used
/// anyway, for two reasons that do apply: a report can be tens of thousands of rows, where
/// reflection based writing is measurably slower and allocates far more; and going through this
/// context pins the wire format of the module to snake_case regardless of what the host configured
/// for its default binder, so a contract test asserts against a format that cannot drift.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TimeSeriesResponse))]
[JsonSerializable(typeof(BreakdownResponse))]
[JsonSerializable(typeof(FunnelSummary))]
[JsonSerializable(typeof(AttributionQualityResponse))]
[JsonSerializable(typeof(TimeSeriesPoint))]
[JsonSerializable(typeof(List<TimeSeriesPoint>))]
[JsonSerializable(typeof(TenantExportManifest))]
public sealed partial class AnalyticsJsonContext : JsonSerializerContext;
