using System.Buffers.Binary;

namespace Dle.Crypto;

/// <summary>
/// Framing for composite key material and composite signatures: a four byte big endian length,
/// then the classical component, then the post-quantum component.
/// </summary>
/// <remarks>
/// The IETF composite signature draft concatenates two fixed size signatures and lets the
/// algorithm identifier imply the split. An explicit length is used here instead, because the
/// engine's own token format already names the algorithm and an explicit length keeps a mismatched
/// parameter set from being parsed as a valid signature of the wrong shape. This is engine
/// internal framing and is not claimed to be wire compatible with that draft.
/// </remarks>
internal static class CompositeEncoding
{
    /// <summary>Size of the length prefix in bytes.</summary>
    internal const int PrefixLength = sizeof(uint);

    /// <summary>Joins the two components.</summary>
    /// <param name="first">The classical component.</param>
    /// <param name="second">The post-quantum component.</param>
    /// <returns>The framed value.</returns>
    internal static byte[] Join(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] result = new byte[PrefixLength + first.Length + second.Length];

        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)first.Length);
        first.CopyTo(result.AsSpan(PrefixLength));
        second.CopyTo(result.AsSpan(PrefixLength + first.Length));

        return result;
    }

    /// <summary>
    /// Splits a framed value. Never throws: it runs on untrusted signatures.
    /// </summary>
    /// <param name="value">The framed value.</param>
    /// <param name="first">The classical component on success, empty otherwise.</param>
    /// <param name="second">The post-quantum component on success, empty otherwise.</param>
    /// <returns><see langword="false"/> when the value is too short or the prefix does not
    /// describe a split that fits.</returns>
    internal static bool TrySplit(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> first, out ReadOnlySpan<byte> second)
    {
        first = default;
        second = default;

        if (value.Length < PrefixLength)
        {
            return false;
        }

        uint firstLength = BinaryPrimitives.ReadUInt32BigEndian(value);

        if (firstLength > (uint)(value.Length - PrefixLength))
        {
            return false;
        }

        first = value.Slice(PrefixLength, (int)firstLength);
        second = value[(PrefixLength + (int)firstLength)..];
        return true;
    }
}
