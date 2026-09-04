using System.Text.Json.Serialization;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Source generated serialization metadata for the cached types that the shared kernel's own context
/// does not cover.
/// </summary>
/// <remarks>
/// <c>DleDomainJsonContext</c> covers <see cref="LinkSnapshot"/>, which is the entry that matters for
/// latency. The two types here are cached by the edge as well — the association documents behind
/// <c>/.well-known/*</c> and the per-host runtime configuration — and every value that reaches the L2
/// cache has to be serializable without reflection (SHARED-KERNEL §17.3), so they get a context in the
/// project that caches them rather than an addition to the binding contract.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WellKnownDocument))]
[JsonSerializable(typeof(DomainRuntimeConfig))]
internal sealed partial class FastPersistenceJsonContext : JsonSerializerContext;
