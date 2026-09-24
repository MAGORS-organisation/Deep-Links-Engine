using Dle.Crypto;
using Dle.Domain.Primitives;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// A generated slug is exactly eight base62 characters, is recoverable with the key, and reveals
/// neither the order nor the volume of link creation (ADR-007).
/// </summary>
public sealed class SlugGeneratorTests
{
    private static SlugGenerator Generator(byte[] key) =>
        new(new FeistelPermutation(key, FeistelPermutation.SlugBits));

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(12_345L)]
    [InlineData(1_000_000_000L)]
    [InlineData(SlugGenerator.MaxSequenceValue)]
    public void FromSequence_IsExactlyEightBase62Characters(long sequence)
    {
        string slug = Generator(CryptoTestKeys.Primary).FromSequence(sequence);

        Assert.Equal(SlugPolicy.GeneratedLength, slug.Length);
        Assert.Equal(8, slug.Length);

        foreach (char c in slug)
        {
            Assert.True(Base62.Alphabet.Contains(c, StringComparison.Ordinal), $"'{c}' is not a base62 digit");
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(999L)]
    [InlineData(123_456_789L)]
    [InlineData(SlugGenerator.MaxSequenceValue)]
    public void TryRecoverSequence_WithTheSameKey_ReturnsTheOriginalSequence(long sequence)
    {
        SlugGenerator generator = Generator(CryptoTestKeys.Primary);

        string slug = generator.FromSequence(sequence);

        Assert.True(generator.TryRecoverSequence(slug, out long recovered));
        Assert.Equal(sequence, recovered);
    }

    [Fact]
    public void FromSequence_OverALargeRun_NeverRepeatsASlug()
    {
        SlugGenerator generator = Generator(CryptoTestKeys.Primary);
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        for (long sequence = 0; sequence < 50_000; sequence++)
        {
            Assert.True(slugs.Add(generator.FromSequence(sequence)), $"slug repeated at sequence {sequence}");
        }

        Assert.Equal(50_000, slugs.Count);
    }

    [Fact]
    public void FromSequence_WithTwoKeys_ProducesDisjointOutput()
    {
        SlugGenerator first = Generator(CryptoTestKeys.Primary);
        SlugGenerator second = Generator(CryptoTestKeys.Secondary);

        var fromFirst = new HashSet<string>(StringComparer.Ordinal);
        var fromSecond = new HashSet<string>(StringComparer.Ordinal);

        for (long sequence = 0; sequence < 2_000; sequence++)
        {
            string a = first.FromSequence(sequence);
            string b = second.FromSequence(sequence);

            Assert.NotEqual(a, b);

            _ = fromFirst.Add(a);
            _ = fromSecond.Add(b);
        }

        fromFirst.IntersectWith(fromSecond);

        // Two independent keyed permutations over 2^47 sharing any of 2 000 images would mean the
        // key is not separating the two deployments' slug spaces.
        Assert.Empty(fromFirst);
    }

    [Fact]
    public void TryRecoverSequence_WithADifferentKey_DoesNotReturnTheOriginalSequence()
    {
        const long sequence = 424_242L;

        string slug = Generator(CryptoTestKeys.Primary).FromSequence(sequence);

        bool recovered = Generator(CryptoTestKeys.Secondary).TryRecoverSequence(slug, out long other);

        // A slug from another deployment either falls outside the 47-bit domain, or decodes to some
        // unrelated sequence. What it must never do is decode to the sequence that produced it.
        if (recovered)
        {
            Assert.NotEqual(sequence, other);
        }
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(SlugGenerator.MaxSequenceValue + 1)]
    [InlineData(long.MaxValue)]
    public void FromSequence_OutOfRangeSequence_ThrowsLoudly(long sequence) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Generator(CryptoTestKeys.Primary).FromSequence(sequence));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("waytoolongforaslug")]
    [InlineData("abcdefg!")]
    [InlineData("abcdef g")]
    [InlineData("zzzzzzzz")]
    public void TryRecoverSequence_MalformedOrOutOfDomain_ReturnsFalse(string slug)
    {
        Assert.False(Generator(CryptoTestKeys.Primary).TryRecoverSequence(slug, out long recovered));
        Assert.Equal(0L, recovered);
    }

    [Fact]
    public void Constructor_PermutationOfTheWrongWidth_Throws() =>
        Assert.Throws<ArgumentException>(() => new SlugGenerator(new FeistelPermutation(CryptoTestKeys.Primary, bits: 32)));

    [Fact]
    public void Constructor_NullPermutation_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SlugGenerator(null!));

    [Fact]
    public void MaxSequenceValue_IsTheFullFortySevenBitSpace() =>
        Assert.Equal((1L << FeistelPermutation.SlugBits) - 1, SlugGenerator.MaxSequenceValue);
}
