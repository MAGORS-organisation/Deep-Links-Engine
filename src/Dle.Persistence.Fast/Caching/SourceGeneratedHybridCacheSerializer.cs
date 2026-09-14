using System.Buffers;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Caching.Hybrid;

namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// A <c>HybridCache</c> serializer that writes one type through its source generated metadata.
/// </summary>
/// <typeparam name="T">The cached type.</typeparam>
/// <remarks>
/// The default <c>HybridCache</c> serializer resolves properties reflectively, which is the one thing
/// this assembly may not do (SHARED-KERNEL §17.3, ADR-012). Holding the
/// <see cref="JsonTypeInfo{T}"/> instead makes serialization allocation-light, trim-safe and
/// ahead-of-time safe, and it fixes the wire format of an L2 entry to the same snake_case shape the
/// rest of the product uses.
/// </remarks>
internal sealed class SourceGeneratedHybridCacheSerializer<T> : IHybridCacheSerializer<T>
{
    private readonly JsonTypeInfo<T> _typeInfo;

    /// <summary>Creates the serializer.</summary>
    /// <param name="typeInfo">Source generated metadata for <typeparamref name="T"/>.</param>
    internal SourceGeneratedHybridCacheSerializer(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        _typeInfo = typeInfo;
    }

    /// <inheritdoc />
    public T Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source);

        return JsonSerializer.Deserialize(ref reader, _typeInfo)!;
    }

    /// <inheritdoc />
    public void Serialize(T value, IBufferWriter<byte> target)
    {
        using var writer = new Utf8JsonWriter(target);

        JsonSerializer.Serialize(writer, value, _typeInfo);
    }
}
