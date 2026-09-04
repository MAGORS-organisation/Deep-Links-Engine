using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Caching.Hybrid;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Supplies reflection-free <c>HybridCache</c> serializers for every type this product caches.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <c>AddSerializerFactory</c> so that it is consulted before the built-in JSON
/// serializer, which would otherwise serialize a <see cref="LinkSnapshot"/> reflectively on its way
/// into Valkey. Types the two contexts do not know about fall through to the built-in handling, which
/// keeps the factory additive rather than a bottleneck.
/// </para>
/// <para>
/// Both source generated contexts are consulted: the shared kernel's for the types in the binding
/// contract, and this project's for the well-known and domain configuration entries the edge caches.
/// </para>
/// </remarks>
public sealed class SourceGeneratedHybridCacheSerializerFactory : IHybridCacheSerializerFactory
{
    /// <inheritdoc />
    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        JsonTypeInfo? typeInfo =
            DleDomainJsonContext.Default.GetTypeInfo(typeof(T)) ??
            FastPersistenceJsonContext.Default.GetTypeInfo(typeof(T));

        if (typeInfo is JsonTypeInfo<T> typed)
        {
            serializer = new SourceGeneratedHybridCacheSerializer<T>(typed);
            return true;
        }

        serializer = null;
        return false;
    }
}
