namespace Dle.Domain.Serialization;

/// <summary>
/// Source generated serialization metadata for every type that crosses a process boundary on the
/// hot path: the L2 cache entry for a link, the click and SDK events on their way to the writer,
/// the attribution exchange with the SDK and the public key set.
/// </summary>
/// <remarks>
/// <para>
/// Reflection based serialization is forbidden on the hot path (shared kernel §0). Resolve time
/// code therefore serializes through this context, for example
/// <c>JsonSerializer.Serialize(snapshot, DleDomainJsonContext.Default.LinkSnapshot)</c>, which is
/// allocation free at start up and safe to trim and to compile ahead of time.
/// </para>
/// <para>
/// The options on the attribute mirror <see cref="DleJson.Default"/>: a source generated context
/// cannot take a converter instance the way a runtime configured options object can, so the enum
/// handling is requested declaratively with <c>UseStringEnumConverter</c>.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LinkSnapshot))]
[JsonSerializable(typeof(RoutingRule[]))]
[JsonSerializable(typeof(OgMeta))]
[JsonSerializable(typeof(ClickEvent))]
[JsonSerializable(typeof(SdkEvent))]
[JsonSerializable(typeof(AttributionResult))]
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(JwksDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class DleDomainJsonContext : JsonSerializerContext;
