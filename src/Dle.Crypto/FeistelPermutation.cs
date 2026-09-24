using System.Buffers.Binary;

namespace Dle.Crypto;

/// <summary>
/// A keyed Feistel network over a fixed width integer space (ADR-007).
/// </summary>
/// <remarks>
/// <para>
/// The width is not assumed to be even. Eight base62 characters hold 62^8 ≈ 2^47.63 values, so the
/// usable slug space is 47 bits and the halves are 24 and 23 bits wide. A textbook balanced
/// Feistel round cannot swap halves of different widths without truncating one of them, which
/// would destroy the bijection. This implementation uses the alternating form instead: each round
/// masks the round function output to the width of the half it is combined with, and the two
/// widths swap along with the halves. After an even number of rounds the widths are back where
/// they started; after an odd number they are swapped, which is why the final recombination uses
/// the width the state actually ended up with rather than the initial one.
/// </para>
/// <para>
/// Every round is invertible given the key, so the whole network is a bijection for any round
/// count and any width — that is what makes a generated slug collision free by construction
/// instead of by retrying against a unique index.
/// </para>
/// <para>
/// The round function is HMAC-SHA-256 over the round index and the half being fed in. Instances
/// are immutable and every operation is allocation free, so a single instance is safe to share
/// across threads.
/// </para>
/// </remarks>
public sealed class FeistelPermutation : IFeistelPermutation
{
    /// <summary>Width of the slug space: 62^8 is just above 2^47, so 47 bits is the largest space
    /// that always fits into eight base62 characters (ADR-007).</summary>
    public const int SlugBits = 47;

    /// <summary>Round count specified by ADR-007.</summary>
    public const int DefaultRounds = 4;

    /// <summary>Smallest width the construction is offered for.</summary>
    private const int MinBits = 8;

    /// <summary>Largest width, bounded by the 64 bit state.</summary>
    private const int MaxBits = 64;

    /// <summary>Length of the round function input: one round index byte and eight value bytes.</summary>
    private const int RoundInputLength = 9;

    private readonly byte[] _key;
    private readonly int _rounds;
    private readonly int _leftBits;
    private readonly int _rightBits;
    private readonly int _finalLeftBits;
    private readonly int _finalRightBits;

    /// <summary>
    /// Creates a permutation.
    /// </summary>
    /// <param name="key">The HMAC key. At least 16 bytes; 32 is what
    /// <see cref="CryptoKeyDerivation"/> produces.</param>
    /// <param name="bits">Width of the permuted space, from 8 to 64.</param>
    /// <param name="rounds">Number of rounds. Four is the ADR-007 value.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is shorter than 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> or
    /// <paramref name="rounds"/> is outside its allowed range.</exception>
    public FeistelPermutation(ReadOnlySpan<byte> key, int bits = SlugBits, int rounds = DefaultRounds)
    {
        if (key.Length < 16)
        {
            throw new ArgumentException("A Feistel key must be at least 16 bytes.", nameof(key));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bits, MinBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, MaxBits);
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 32);

        _key = key.ToArray();
        _rounds = rounds;

        // The wider half goes on the left so that a 47 bit space splits 24 / 23.
        _leftBits = (bits + 1) / 2;
        _rightBits = bits - _leftBits;

        bool swapped = (rounds & 1) == 1;
        _finalLeftBits = swapped ? _rightBits : _leftBits;
        _finalRightBits = swapped ? _leftBits : _rightBits;

        Bits = bits;
    }

    /// <inheritdoc />
    public int Bits { get; }

    /// <summary>Largest value the permutation is defined for.</summary>
    public ulong MaxValue => Mask(Bits);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> does not fit into
    /// <see cref="Bits"/> bits. A value outside the domain has no image, and silently reducing it
    /// would break the bijection, so it is refused rather than wrapped.</exception>
    public ulong Encrypt(ulong value)
    {
        ThrowIfOutsideDomain(value);

        int leftBits = _leftBits;
        int rightBits = _rightBits;
        ulong left = rightBits == 64 ? 0 : value >> rightBits;
        ulong right = value & Mask(rightBits);

        for (int round = 0; round < _rounds; round++)
        {
            ulong mixed = left ^ (RoundFunction(round, right) & Mask(leftBits));

            left = right;
            right = mixed;
            (leftBits, rightBits) = (rightBits, leftBits);
        }

        return Combine(left, right, rightBits);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> does not fit into
    /// <see cref="Bits"/> bits.</exception>
    public ulong Decrypt(ulong value)
    {
        ThrowIfOutsideDomain(value);

        int leftBits = _finalLeftBits;
        int rightBits = _finalRightBits;
        ulong left = rightBits == 64 ? 0 : value >> rightBits;
        ulong right = value & Mask(rightBits);

        for (int round = _rounds - 1; round >= 0; round--)
        {
            // Before the round, the right half was what is now the left half, and the left half
            // was the current right half with the round function removed again. Its width is the
            // current right width, because the widths swapped when the round ran.
            ulong previousRight = left;
            ulong previousLeft = right ^ (RoundFunction(round, previousRight) & Mask(rightBits));

            left = previousLeft;
            right = previousRight;
            (leftBits, rightBits) = (rightBits, leftBits);
        }

        return Combine(left, right, rightBits);
    }

    /// <summary>Reassembles the two halves into one value.</summary>
    private static ulong Combine(ulong left, ulong right, int rightBits) =>
        rightBits == 64 ? right : (left << rightBits) | right;

    /// <summary>Mask of the low <paramref name="bits"/> bits, defined for a full 64 bit width too,
    /// where <c>1UL &lt;&lt; 64</c> would silently wrap to one.</summary>
    private static ulong Mask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    private void ThrowIfOutsideDomain(ulong value)
    {
        if (Bits < 64 && value > Mask(Bits))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Value does not fit into {Bits.ToString(CultureInfo.InvariantCulture)} bits.");
        }
    }

    /// <summary>
    /// The round function: HMAC-SHA-256 over the round index and the half, folded to 64 bits. The
    /// round index is part of the message so that two rounds fed the same half produce unrelated
    /// output, which is what stops the network from collapsing into an involution.
    /// </summary>
    private ulong RoundFunction(int round, ulong input)
    {
        Span<byte> message = stackalloc byte[RoundInputLength];
        message[0] = (byte)round;
        BinaryPrimitives.WriteUInt64BigEndian(message[1..], input);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, message, digest);

        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }
}
