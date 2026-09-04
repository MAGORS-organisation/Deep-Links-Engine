namespace Dle.Crypto;

/// <summary>
/// Turns a database sequence value into a slug: sequence, keyed permutation, base62 over exactly
/// eight characters (ADR-007).
/// </summary>
/// <remarks>
/// The sequence is the only source of uniqueness; the permutation only hides it. That is the whole
/// point of ADR-007 — because the mapping is a bijection, creating a link needs no retry loop
/// against a unique index and has a deterministic response time, while the slug still leaks
/// neither the order nor the volume of link creation.
/// </remarks>
public sealed class SlugGenerator : ISlugGenerator
{
    /// <summary>Largest sequence value that fits into eight base62 characters.</summary>
    public const long MaxSequenceValue = (1L << FeistelPermutation.SlugBits) - 1;

    private readonly IFeistelPermutation _permutation;

    /// <summary>
    /// Creates a generator.
    /// </summary>
    /// <param name="permutation">The keyed permutation. Its width must be
    /// <see cref="FeistelPermutation.SlugBits"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The permutation is not 47 bits wide, in which case its
    /// image would not fit exactly into eight base62 characters.</exception>
    public SlugGenerator(IFeistelPermutation permutation)
    {
        ArgumentNullException.ThrowIfNull(permutation);

        if (permutation.Bits != FeistelPermutation.SlugBits)
        {
            throw new ArgumentException(
                $"A slug permutation must be {FeistelPermutation.SlugBits.ToString(CultureInfo.InvariantCulture)} bits wide.",
                nameof(permutation));
        }

        _permutation = permutation;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sequenceValue"/> is negative or above <see cref="MaxSequenceValue"/>. The
    /// sequence is declared as 47 bits in the schema; a value outside that range means the
    /// sequence was misconfigured, and quietly folding it would start handing out slugs that
    /// collide with ones already issued.
    /// </exception>
    public string FromSequence(long sequenceValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequenceValue, MaxSequenceValue);

        ulong permuted = _permutation.Encrypt((ulong)sequenceValue);

        return Base62.Encode(permuted, SlugPolicy.GeneratedLength);
    }

    /// <inheritdoc />
    public bool TryRecoverSequence(string slug, out long sequenceValue)
    {
        sequenceValue = 0;

        if (string.IsNullOrEmpty(slug) || slug.Length != SlugPolicy.GeneratedLength)
        {
            return false;
        }

        if (!Base62.TryDecode(slug, out ulong permuted))
        {
            return false;
        }

        // Eight base62 characters reach 62^8, which is larger than the 47 bit space. Values above
        // it are outside the permutation's domain: they are custom slugs that happen to look
        // generated, not slugs this generator ever produced.
        if (permuted > (ulong)MaxSequenceValue)
        {
            return false;
        }

        sequenceValue = (long)_permutation.Decrypt(permuted);
        return true;
    }
}
