using Dle.Domain.Attribution;
using Xunit;

namespace Dle.UnitTests.Attribution;

/// <summary>
/// FR-184 and TC-148. The claim code is read off an interstitial by a human and typed into an
/// application, so the alphabet deliberately omits every confusable glyph. "O" must not be silently
/// mapped onto "0": the code the user reads never contains either, and a quiet mapping would turn a
/// typo into someone else's attribution.
/// </summary>
public sealed class ClaimCodeTests
{
    [Fact]
    public void Alphabet_OmitsEveryConfusableGlyph()
    {
        Assert.Equal("ACDEFGHJKLMNPQRTUVWXY34679", ClaimCode.Alphabet);

        foreach (char confusable in "BIOSZ01258")
        {
            Assert.DoesNotContain(confusable, ClaimCode.Alphabet);
        }
    }

    [Fact]
    public void Length_IsSix()
    {
        Assert.Equal(6, ClaimCode.Length);
    }

    [Theory]
    [InlineData("acdefg", "ACDEFG")]
    [InlineData("ACD-EFG", "ACDEFG")]
    [InlineData(" ACD EFG ", "ACDEFG")]
    [InlineData("acd-efg", "ACDEFG")]
    [InlineData("A C-D E F G", "ACDEFG")]
    public void Normalize_UppercasesAndStripsSeparators(string raw, string expected)
    {
        Assert.Equal(expected, ClaimCode.Normalize(raw));
    }

    [Fact]
    public void Normalize_DoesNotMapOToZero()
    {
        // "O" is not in the alphabet, so a code containing it is a typo and must fail validation
        // rather than be repaired into a different, possibly valid, code.
        string normalized = ClaimCode.Normalize("ACDEFO");

        Assert.Equal("ACDEFO", normalized);
        Assert.False(ClaimCode.IsWellFormed(normalized));
    }

    [Fact]
    public void Normalize_EmptyInput_IsEmpty()
    {
        Assert.Equal(string.Empty, ClaimCode.Normalize(string.Empty));
        Assert.Equal(string.Empty, ClaimCode.Normalize("   "));
        Assert.Equal(string.Empty, ClaimCode.Normalize("---"));
    }

    [Fact]
    public void Normalize_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ClaimCode.Normalize(null!));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        string once = ClaimCode.Normalize("acd-efg");

        Assert.Equal(once, ClaimCode.Normalize(once));
    }

    [Theory]
    [InlineData("ACDEFG", true)]
    [InlineData("34679A", true)]
    [InlineData("XYWVUT", true)]
    [InlineData("ABCDEF", false)]   // B is not in the alphabet
    [InlineData("ACDEF0", false)]   // zero is not in the alphabet
    [InlineData("ACDEF1", false)]
    [InlineData("ACDEF8", false)]
    [InlineData("acdefg", false)]   // must be normalised first
    [InlineData("ACDEF", false)]    // too short
    [InlineData("ACDEFGH", false)]  // too long
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormed_ChecksLengthAndAlphabet(string? code, bool expected)
    {
        Assert.Equal(expected, ClaimCode.IsWellFormed(code));
    }
}
