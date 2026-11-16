using System.Text.Json.Serialization;

namespace Dle.Crypto;

/// <summary>
/// Source generated serialization for the token payload.
/// </summary>
/// <remarks>
/// Reflection based serialization is not allowed on the hot path, and token
/// validation is on it. The shared kernel context does not cover
/// <see cref="SignedTokenPayload"/> because the payload shape belongs to this module, so the
/// module carries its own context rather than widening the kernel's.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SignedTokenPayload))]
public sealed partial class DleCryptoJsonContext : JsonSerializerContext;
