using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dle.Edge.Rendering;

/// <summary>
/// Source generated serialization metadata for everything the rendering slice parses.
/// </summary>
/// <remarks>
/// Branding is read on the interstitial and QR paths, both of which sit behind the same cache as the
/// resolve path, so the reflection-based serializer is not an option here (SHARED-KERNEL §17.3). The
/// context is deliberately tiny: it covers the one document the edge deserializes and nothing else.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PageBranding.BrandingDocument))]
internal sealed partial class RenderingJsonContext : JsonSerializerContext;
