using System.Text.Json.Serialization;

using Dle.Domain.Contracts;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Source generated serialization metadata for the webhook envelope and the module's API surface.
/// </summary>
/// <remarks>
/// <para>
/// The options mirror <c>DleJson.Default</c>, so a payload produced here is spelled exactly like
/// every other DLE payload: snake_case members, string enumerations, null members omitted.
/// </para>
/// <para>
/// A source generated context rather than reflection, for a reason that goes beyond the shared
/// kernel's general rule: the bytes this produces are the bytes that get signed and sent, and a
/// retry has to reproduce them exactly. Deterministic, generated writing is what makes that
/// dependable — and it keeps the assembly trimmable, which a control plane inherits from the same
/// discipline the edge is held to (SHARED-KERNEL §0, TC-165).
/// </para>
/// <para>
/// The management contracts are listed here too, so the CRUD endpoints write through the same
/// context rather than through whatever the host configured for its default binder. That keeps the
/// wire format of this module stable regardless of another slice's serializer options.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(CreateWebhookRequest))]
[JsonSerializable(typeof(WebhookResponse))]
[JsonSerializable(typeof(List<WebhookResponse>))]
[JsonSerializable(typeof(WebhookCreatedResponse))]
[JsonSerializable(typeof(List<WebhookDeliveryResponse>))]
[JsonSerializable(typeof(TestWebhookResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class WebhookJsonContext : JsonSerializerContext;
