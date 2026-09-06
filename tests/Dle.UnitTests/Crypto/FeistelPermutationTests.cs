using Dle.Crypto;
using Dle.Domain.Crypto;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// ADR-007 makes the slug space collision free <em>by construction</em>: the sequence to slug
/// mapping is a keyed permutation, so link creation needs no retry against a unique index. That
/// guarantee is worth exactly as much as the bijection is real. A defect here is silent — two
/// campaigns would quietly receive the same slug months after launch — so the property is proved
/// by exhaustive enumeration at every width where enumeration is affordable, at both parities of
/// width and of round count, and sampled at the production width of 47 bits.
/// </summary>
public sealed class FeistelPermutationTests
{
    /// <summary>
    /// Widths small enough to enumerate completely. Both parities are present because the halves
    /// are unequal for an odd width, which is the case a textbook balanced Feistel gets wrong.
    /// </summary>
    public static TheoryData<int, int> ExhaustiveWidths()
    {
        TheoryData<int, int> data = [];

        foreach (int bits in new[] { 8, 9, 12, 13 })
        {
            // Round counts of both parities: after an odd number of rounds the two half widths are
            // swapped, which is the branch the final recombination has to get right.
            foreach (int rounds in new[] { 2, 3, 4, 5 })
            {
                data.Add(bits, rounds);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExhaustiveWidths))]
    public void Encrypt_OverTheWholeNarrowDomain_IsABijection(int bits, int rounds)
    {
        var permutation = new FeistelPermutation(CryptoTestKeys.Primary, bits, rounds);

        int size = 1 << bits;
        bool[] reached = new bool[size];
        int imageCount = 0;

        for (int value = 0; value < size; value++)
        {
            ulong image = permutation.Encrypt((ulong)value);

            // Closed: the image never leaves the domain, so eight base62 characters always suffice.
            Assert.InRange(image, 0UL, (ulong)(size - 1));

            // Injective: no two sequence values may produce the same slug.
            Assert.False(reached[(int)image], $"value {value} collided on image {image}");

            reached[(int)image] = true;
            imageCount++;

            // Invertible: the sequence is recoverable, which is what makes a slug auditable.
            Assert.Equal((ulong)value, permutation.Decrypt(image));
        }

        // Surjective: every slug in the space is reachable, so none of the space is wasted.
        Assert.Equal(size, imageCount);
        Assert.True(Array.TrueForAll(reached, wasReached => wasReached), "some values were unreachable");
    }

    /// <summary>
    /// One wider exhaustive pass. 65 536 values is still under a second and covers a width whose
    /// halves (8 and 8) exercise the equal-halves path at a realistic size.
    /// </summary>
    [Fact]
    public void Encrypt_OverSixteenBits_IsABijection()
    {
        var permutation = new FeistelPermutation(CryptoTestKeys.Primary, bits: 16, rounds: FeistelPermutation.DefaultRounds);

        const int size = 1 << 16;
        bool[] reached = new bool[size];

        for (int value = 0; value < size; value++)
        {
            ulong image = permutation.Encrypt((ulong)value);

            Assert.InRange(image, 0UL, size - 1UL);
            Assert.False(reached[(int)image]);

            reached[(int)image] = true;
        }

        Assert.True(Array.TrueForAll(reached, wasReached => wasReached));
    }

    /// <summary>
    /// The production width. Exhaustive enumeration of 2^47 is not possible, so the round trip is
    /// asserted over a large deterministic sample that includes both ends of the domain.
    /// </summary>
    [Fact]
    public void Encrypt_AtTheProductionWidth_RoundTripsAndStaysInDomain()
    {
        var permutation = new FeistelPermutation(CryptoTestKeys.Primary);
        ulong max = (1UL << FeistelPermutation.SlugBits) - 1;

        Assert.Equal(FeistelPermutation.SlugBits, permutation.Bits);
        Assert.Equal(max, permutation.MaxValue);

        CryptoTestKeys.Sequence random = new(seed: 0x5DEECE66DUL);
        var images = new HashSet<ulong>();

        // The two boundary values plus a sample. The sample size is chosen so the test stays well
        // inside a second while still being large enough for a duplicate image to be surprising.
        const int sampleSize = 200_000;

        for (int i = 0; i < sampleSize; i++)
        {
            ulong value = i switch
            {
                0 => 0UL,
                1 => max,
                2 => 1UL,
                3 => max - 1,
                _ => random.Next(max),
            };

            ulong image = permutation.Encrypt(value);

            Assert.InRange(image, 0UL, max);
            Assert.Equal(value, permutation.Decrypt(image));

            _ = images.Add(image);
        }

        // Distinct inputs must give distinct images. The sample draws with replacement, so the
        // count is only a lower bound; a genuine collision would show up as a large shortfall.
        Assert.True(images.Count > sampleSize - 1000, $"only {images.Count} distinct images from {sampleSize} draws");
    }

    [Fact]
    public void Decrypt_UndoesEncrypt_ForEveryRoundCount()
    {
        for (int rounds = 2; rounds <= 8; rounds++)
        {
            var permutation = new FeistelPermutation(CryptoTestKeys.Primary, FeistelPermutation.SlugBits, rounds);

            foreach (ulong value in new ulong[] { 0, 1, 42, 1_000_000, (1UL << 46) + 7 })
            {
                Assert.Equal(value, permutation.Decrypt(permutation.Encrypt(value)));
            }
        }
    }

    [Fact]
    public void Encrypt_WithADifferentKey_ProducesADifferentPermutation()
    {
        var first = new FeistelPermutation(CryptoTestKeys.Primary);
        var second = new FeistelPermutation(CryptoTestKeys.Secondary);

        int agreements = 0;

        for (ulong value = 0; value < 1000; value++)
        {
            if (first.Encrypt(value) == second.Encrypt(value))
            {
                agreements++;
            }
        }

        // Two independent permutations of a 2^47 space agreeing on any of a thousand points would
        // mean the key is not reaching the round function.
        Assert.Equal(0, agreements);
    }

    [Fact]
    public void Encrypt_ValueOutsideTheDomain_Throws()
    {
        var permutation = new FeistelPermutation(CryptoTestKeys.Primary, bits: 12);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => permutation.Encrypt(1UL << 12));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => permutation.Decrypt(1UL << 12));
    }

    [Fact]
    public void Constructor_ShortKey_Throws() =>
        Assert.Throws<ArgumentException>(() => new FeistelPermutation(new byte[15]));

    [Theory]
    [InlineData(7)]
    [InlineData(65)]
    public void Constructor_WidthOutsideRange_Throws(int bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeistelPermutation(CryptoTestKeys.Primary, bits));

    [Theory]
    [InlineData(1)]
    [InlineData(33)]
    public void Constructor_RoundCountOutsideRange_Throws(int rounds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeistelPermutation(CryptoTestKeys.Primary, FeistelPermutation.SlugBits, rounds));

    [Fact]
    public void Permutation_IsTheContractedInterface() =>
        Assert.IsAssignableFrom<IFeistelPermutation>(new FeistelPermutation(CryptoTestKeys.Primary));
}
