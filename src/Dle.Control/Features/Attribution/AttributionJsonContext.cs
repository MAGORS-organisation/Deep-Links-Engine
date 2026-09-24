using System.Text.Json.Serialization;

using Dle.Domain.Contracts;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Source generated serialization metadata for the SDK facing payloads of this module.
/// </summary>
/// <remarks>
/// <para>
/// SHARED-KERNEL §17.3 forbids reflection based serialization on the hot path, and these endpoints
/// are the SDK's half of it: <c>POST /v1/events</c> alone is allowed sixty calls a minute per
/// installation. The handlers therefore read and write through this context explicitly instead of
/// relying on the framework's default binder, which also keeps the wire format snake_case in this
/// module regardless of what any other slice configures globally.
/// </para>
/// <para>
/// The options mirror <see cref="Dle.Domain.Serialization.DleJson.Default"/>. Dictionary keys keep
/// their spelling: UTM parameters and evidence keys are data, and renaming them would corrupt them.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResolveRequestDto))]
[JsonSerializable(typeof(ResolveResponseDto))]
[JsonSerializable(typeof(EventBatchDto))]
[JsonSerializable(typeof(EventBatchAcceptedDto))]
[JsonSerializable(typeof(IssueClaimCodeRequest))]
[JsonSerializable(typeof(ClaimCodeResponse))]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class AttributionJsonContext : JsonSerializerContext;
