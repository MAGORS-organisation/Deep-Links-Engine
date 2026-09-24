using System.Buffers;
using System.Text;

namespace Dle.Domain.Routing;

/// <summary>
/// Deterministic A/B bucketing derived from the click identifier (FR-125).
/// </summary>
/// <remarks>
/// <para>
/// There is no randomness anywhere in the routing pipeline. The bucket is a pure function of the click
/// id, so the same click always lands in the same variant — on every node, in the rule simulator
/// (FR-129) and when a decision is replayed months later from the click stream.
/// </para>
/// <para>
/// The hash is FNV-1a over the UTF-8 bytes of the click id, reduced modulo 100. FNV-1a is chosen for
/// being tiny, allocation free and stable across runtimes; it is explicitly not a cryptographic hash
/// and must never be used to protect a secret.
/// </para>
/// </remarks>
public static class ConsistentBucket
{
    /// <summary>Number of buckets the space is divided into, matching the percentage scale of <see cref="AbVariant.Percent"/>.</summary>
    public const int BucketCount = 100;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const int StackBufferSize = 256;

    /// <summary>
    /// Computes the bucket a click id belongs to.
    /// </summary>
    /// <param name="clickId">The click identifier. An empty string is allowed and yields a fixed bucket.</param>
    /// <returns>A value in the range 0..99.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clickId"/> is <see langword="null"/>.</exception>
    public static short Of(string clickId)
    {
        ArgumentNullException.ThrowIfNull(clickId);

        if (clickId.Length == 0)
        {
            return (short)(FnvOffsetBasis % BucketCount);
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(clickId.Length);

        byte[]? rented = null;
        scoped Span<byte> buffer;

        if (maxBytes <= StackBufferSize)
        {
            buffer = stackalloc byte[StackBufferSize];
        }
        else
        {
            rented = ArrayPool<byte>.Shared.Rent(maxBytes);
            buffer = rented;
        }

        try
        {
            int written = Encoding.UTF8.GetBytes(clickId, buffer);
            return (short)(Hash(buffer[..written]) % BucketCount);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong hash = FnvOffsetBasis;

        foreach (byte b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }
}
