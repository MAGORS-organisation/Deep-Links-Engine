using System.Buffers.Binary;

namespace Dle.Crypto;

/// <summary>
/// Encodes and decodes click identifiers that carry their own timestamp (§B.6.3).
/// </summary>
/// <remarks>
/// <para>
/// <c>click_events</c> is partitioned by <c>occurred_at</c>. A lookup by <c>click_id</c> with no
/// time predicate cannot prune partitions, so its cost grows linearly with retention and the
/// regression only becomes visible after months of operation. Embedding the instant in the
/// identifier lets the attribution service derive <c>occurred_at BETWEEN t0 - 5 min AND
/// t0 + 5 min</c> and touch one partition instead of all of them.
/// </para>
/// <para>
/// The identifier is public — it travels in the Play install referrer and in query parameters — so
/// it is authenticated, not secret. The layout is 42 bits of milliseconds since 2024-01-01, 22
/// bits of sequence, put through a keyed 64 bit Feistel permutation so that neither the time nor
/// the volume of clicks can be read off it, followed by a 32 bit MAC over the permuted value.
/// Encrypt-then-MAC is what makes tampering fail loudly: without the MAC a mutated identifier
/// would still decode, just to a different and entirely plausible instant, and the attribution
/// service would prune to the wrong partition and silently find nothing (T-05, TC-167).
/// </para>
/// </remarks>
public sealed class ClickIdCodec : IClickIdCodec
{
    /// <summary>Length of an identifier in characters. 96 bits need 17 base62 digits.</summary>
    public const int Length = 17;

    /// <summary>Bits reserved for the timestamp: 2^42 milliseconds is about 139 years.</summary>
    private const int TimestampBits = 42;

    /// <summary>Bits reserved for the per millisecond sequence.</summary>
    private const int SequenceBits = 22;

    /// <summary>Bits of MAC. A forged identifier passes with probability 2^-32.</summary>
    private const int MacBits = 32;

    private const ulong SequenceMask = (1UL << SequenceBits) - 1;
    private const ulong TimestampMask = (1UL << TimestampBits) - 1;
    private const int MacBytes = MacBits / 8;

    /// <summary>Total payload width, which bounds the decoded value.</summary>
    private static readonly UInt128 Domain = UInt128.One << (TimestampBits + SequenceBits + MacBits);

    /// <summary>
    /// Epoch of the timestamp field. A custom epoch buys the 25 bits that the Unix epoch would
    /// waste on time nobody will ever encode here.
    /// </summary>
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FeistelPermutation _permutation;
    private readonly byte[] _macKey;
    private long _sequence;

    /// <summary>
    /// Creates a codec.
    /// </summary>
    /// <param name="permutationKey">Key of the 64 bit permutation.</param>
    /// <param name="macKey">Key of the MAC. Distinct from <paramref name="permutationKey"/>, so
    /// that recovering one does not yield the other.</param>
    /// <param name="rounds">Number of Feistel rounds.</param>
    public ClickIdCodec(ReadOnlySpan<byte> permutationKey, ReadOnlySpan<byte> macKey, int rounds = FeistelPermutation.DefaultRounds)
    {
        if (macKey.Length < 16)
        {
            throw new ArgumentException("A click identifier MAC key must be at least 16 bytes.", nameof(macKey));
        }

        _permutation = new FeistelPermutation(permutationKey, TimestampBits + SequenceBits, rounds);
        _macKey = macKey.ToArray();

        // Starting the counter at a random point means two instances that come up in the same
        // millisecond do not immediately hand out the same sequence values.
        _sequence = RandomNumberGenerator.GetInt32(1 << 20);
    }

    /// <summary>Instant the timestamp field is measured from.</summary>
    public static DateTimeOffset TimestampEpoch => Epoch;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occurredAt"/> is before
    /// <see cref="TimestampEpoch"/> or more than 2^42 milliseconds after it.</exception>
    public string New(DateTimeOffset occurredAt)
    {
        long milliseconds = (long)(occurredAt.ToUniversalTime() - Epoch).TotalMilliseconds;

        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds, nameof(occurredAt));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((ulong)milliseconds, TimestampMask, nameof(occurredAt));

        ulong sequence = (ulong)Interlocked.Increment(ref _sequence) & SequenceMask;
        ulong core = ((ulong)milliseconds << SequenceBits) | sequence;
        ulong permuted = _permutation.Encrypt(core);
        uint mac = ComputeMac(permuted);

        UInt128 value = ((UInt128)permuted << MacBits) | mac;

        return Base62Wide.Encode(value, Length);
    }

    /// <inheritdoc />
    public bool TryDecode(string clickId, out DateTimeOffset occurredAt, out long sequence)
    {
        occurredAt = default;
        sequence = 0;

        if (string.IsNullOrEmpty(clickId) || clickId.Length != Length)
        {
            return false;
        }

        if (!Base62Wide.TryDecode(clickId, out UInt128 value) || value >= Domain)
        {
            return false;
        }

        ulong permuted = (ulong)(value >> MacBits);
        uint presentedMac = (uint)(value & uint.MaxValue);

        if (!MacMatches(permuted, presentedMac))
        {
            return false;
        }

        ulong core = _permutation.Decrypt(permuted);

        occurredAt = Epoch.AddMilliseconds(core >> SequenceBits);
        sequence = (long)(core & SequenceMask);
        return true;
    }

    /// <summary>Truncated HMAC-SHA-256 over the permuted value.</summary>
    private uint ComputeMac(ulong permuted)
    {
        Span<byte> message = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(message, permuted);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(_macKey, message, digest);

        return BinaryPrimitives.ReadUInt32BigEndian(digest);
    }

    /// <summary>
    /// Compares the presented MAC in constant time (T-17, S-03). The comparison is short, but an
    /// early exit here would still turn forgery into a byte at a time search.
    /// </summary>
    private bool MacMatches(ulong permuted, uint presentedMac)
    {
        Span<byte> expected = stackalloc byte[MacBytes];
        Span<byte> presented = stackalloc byte[MacBytes];

        BinaryPrimitives.WriteUInt32BigEndian(expected, ComputeMac(permuted));
        BinaryPrimitives.WriteUInt32BigEndian(presented, presentedMac);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
