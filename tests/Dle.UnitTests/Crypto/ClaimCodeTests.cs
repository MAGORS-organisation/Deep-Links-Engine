using Dle.Crypto;
using Dle.Domain.Attribution;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// A claim code is read off a screen and typed into a phone (FR-184, TC-148). Everything about it
/// follows from that: the alphabet excludes characters people confuse, normalisation forgives the
/// separators and the case people add, and the stored form is a keyed hash compared in fixed time.
/// </summary>
public sealed class ClaimCodeTests
{
    /// <summary>
    /// The pairs people mistype: B/8, I/1, O/0, S/5, Z/2. Both halves of every pair are out of the
    /// alphabet, which is why a code can be read aloud over a telephone without a spelling alphabet.
    /// </summary>
    private const string Confusables = "BIOSZ01258";

    [Fact]
    public void Alphabet_ExcludesConfusableCharacters()
    {
        foreach (char c in Confusables)
        {
            Assert.False(
                ClaimCode.Alphabet.Contains(c, StringComparison.Ordinal),
                $"'{c}' is confusable and must not be in the claim code alphabet");
        }
    }

    [Fact]
    public void Alphabet_IsUppercaseAsciiAndDigitsOnly()
    {
        foreach (char c in ClaimCode.Alphabet)
        {
            Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c), $"'{c}' is not an uppercase ASCII letter or digit");
        }

        Assert.Equal(ClaimCode.Alphabet.Length, ClaimCode.Alphabet.Distinct().Count());
    }

    [Theory]
    [InlineData("acd efg", "ACDEFG")]
    [InlineData("acd-efg", "ACDEFG")]
    [InlineData("  ACD-EFG  ", "ACDEFG")]
    [InlineData("a c d e f g", "ACDEFG")]
    [InlineData("ACDEFG", "ACDEFG")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("---", "")]
    public void Normalize_StripsSeparatorsAndUppercases(string raw, string expected) =>
        Assert.Equal(expected, ClaimCode.Normalize(raw));

    [Theory]
    [InlineData("acd-efg")]
    [InlineData("  A C D E F G ")]
    [InlineData("ACDEFG")]
    [InlineData("nonsense with spaces")]
    [InlineData("")]
    public void Normalize_IsIdempotent(string raw)
    {
        string once = ClaimCode.Normalize(raw);

        Assert.Equal(once, ClaimCode.Normalize(once));
        Assert.Equal(once, ClaimCode.Normalize(ClaimCode.Normalize(once)));
    }

    [Fact]
    public void Normalize_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ClaimCode.Normalize(null!));

    [Theory]
    [InlineData("ACDEFG", true)]
    [InlineData("34679A", true)]
    [InlineData("acdefg", false)]
    [InlineData("ACDEF", false)]
    [InlineData("ACDEFGH", false)]
    [InlineData("ACDEFB", false)]
    [InlineData("ACDEF0", false)]
    [InlineData("ACDEF-", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormed_ChecksLengthAndAlphabet(string? code, bool expected) =>
        Assert.Equal(expected, ClaimCode.IsWellFormed(code));

    [Fact]
    public void Create_ProducesAWellFormedCodeAndAKeyedHash()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);

        for (int i = 0; i < 200; i++)
        {
            ClaimCodeCredential credential = generator.Create();

            Assert.Equal(ClaimCode.Length, credential.Code.Length);
            Assert.True(ClaimCode.IsWellFormed(credential.Code), $"'{credential.Code}' is not well formed");
            Assert.Equal(32, credential.Hash.Length);
            Assert.True(generator.Verify(credential.Code, credential.Hash));
        }
    }

    [Fact]
    public void Verify_AcceptsTheCodeAsAPersonWouldTypeIt()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);
        ClaimCodeCredential credential = generator.Create();

        string typed = credential.Code[..3].ToLowerInvariant() + "-" + credential.Code[3..].ToLowerInvariant();

        Assert.True(generator.Verify(typed, credential.Hash));
        Assert.True(generator.Verify("  " + credential.Code + " ", credential.Hash));
    }

    [Fact]
    public void Verify_AnotherCode_Fails()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);

        ClaimCodeCredential credential = generator.Create();
        ClaimCodeCredential other = generator.Create();

        Assert.False(generator.Verify(other.Code, credential.Hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACDEF")]
    [InlineData("ACDEFB")]
    [InlineData("!!!!!!")]
    public void Verify_MalformedCode_Fails(string? presented)
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);

        Assert.False(generator.Verify(presented, generator.Create().Hash));
    }

    [Fact]
    public void Verify_EmptyStoredHash_Fails()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);

        Assert.False(generator.Verify(generator.Create().Code, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void HashOf_WithADifferentSecret_Differs()
    {
        var first = new ClaimCodeGenerator(CryptoTestKeys.Primary);
        var second = new ClaimCodeGenerator(CryptoTestKeys.Secondary);

        Assert.NotEqual(first.HashOf("ACDEFG"), second.HashOf("ACDEFG"));
        Assert.False(second.Verify("ACDEFG", first.HashOf("ACDEFG")));
    }

    [Fact]
    public void HashOf_IsStableForTheSameCode()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);

        Assert.Equal(generator.HashOf("ACDEFG"), generator.HashOf("ACDEFG"));
    }

    [Fact]
    public void Constructor_ShortSecret_Throws() =>
        Assert.Throws<ArgumentException>(() => new ClaimCodeGenerator(new byte[15]));

    [Fact]
    public void Verification_UsesFixedTimeEquals() =>
        Assert.True(
            ConstantTimeAssertion.CallsFixedTimeEquals(typeof(ClaimCodeGenerator), nameof(ClaimCodeGenerator.Verify)),
            "ClaimCodeGenerator.Verify must compare with CryptographicOperations.FixedTimeEquals.");

    [Fact]
    public void Create_SpreadsAcrossTheAlphabet()
    {
        var generator = new ClaimCodeGenerator(CryptoTestKeys.Primary);
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 500; i++)
        {
            _ = codes.Add(generator.Create().Code);
        }

        // 26^6 is about 300 million, so 500 draws colliding more than once would mean the rejection
        // sampling is folding the space.
        Assert.True(codes.Count >= 499, $"only {codes.Count} distinct codes out of 500");
    }
}
