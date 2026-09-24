using System.Globalization;
using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// TC-109. A slug is a tenant-scoped identifier that a human reads off a poster. If a Cyrillic "а"
/// were quietly folded onto the Latin "a", two tenants would fight over one address; if a
/// compatibility form were not folded at all, one tenant would own two spellings of one name.
/// SHARED-KERNEL section 1 asks for both: NFKC first, then a strict character allowlist.
/// </summary>
public sealed class SlugPolicyTests
{
    // ---------------------------------------------------------------- confusables must be refused

    [Theory]
    [Trait("TestCase", "TC-109")]
    [InlineData("аbc", "Cyrillic small letter a (U+0430)")]
    [InlineData("pаypal", "Cyrillic small letter a inside a brand name")]
    [InlineData("еxample", "Cyrillic small letter ie (U+0435)")]
    [InlineData("οnline", "Greek small letter omicron (U+03BF)")]
    [InlineData("ѕale", "Cyrillic small letter dze (U+0455)")]
    [InlineData("рromo", "Cyrillic small letter er (U+0440)")]
    [InlineData("ӏnvoice", "Cyrillic palochka (U+04CF)")]
    [InlineData("café", "Latin small letter e with acute")]
    [InlineData("сampaign", "Cyrillic small letter es (U+0441)")]
    public void TryNormalize_ConfusableCharacter_IsRejectedRatherThanFoldedToAscii(string raw, string why)
    {
        bool accepted = SlugPolicy.TryNormalize(raw, out string slug);

        Assert.False(accepted, why + " must not produce a slug at all.");
        Assert.Equal(string.Empty, slug);
    }

    [Fact]
    [Trait("TestCase", "TC-109")]
    public void TryNormalize_CyrillicHomoglyph_DoesNotCollideWithTheLatinSlug()
    {
        Assert.True(SlugPolicy.TryNormalize("promo", out string latin));
        Assert.False(SlugPolicy.TryNormalize("ргоmo", out string cyrillic));

        Assert.Equal("promo", latin);
        Assert.NotEqual(latin, cyrillic);
    }

    // ---------------------------------------------------------------- NFKC

    [Theory]
    [Trait("TestCase", "TC-109")]
    [InlineData("ＰＲＯＭＯ", "promo")]     // full width PROMO
    [InlineData("１２３", "123")]                     // full width digits
    [InlineData("①②", "12")]                            // circled digits one and two
    public void TryNormalize_CompatibilityCharacters_AreFoldedByNfkc(string raw, string expected)
    {
        bool accepted = SlugPolicy.TryNormalize(raw, out string slug);

        Assert.True(
            accepted,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                SHARED-KERNEL section 1 requires NFKC normalisation before the character allowlist,
                so "{raw}" must fold to "{expected}" rather than be refused.

                Root cause if this fails: Directory.Build.props sets InvariantGlobalization=true for
                the whole solution, and in globalization-invariant mode String.Normalize is a no-op
                (it returns the input unchanged and IsNormalized always reports true). The NFKC call
                in SlugPolicy.TryNormalize therefore does nothing at run time and the compatibility
                characters fall through to the ASCII allowlist, which refuses them.
                """));
        Assert.Equal(expected, slug);
    }

    // ---------------------------------------------------------------- ordinary normalisation

    [Theory]
    [InlineData("Promo", "promo")]
    [InlineData("PROMO", "promo")]
    [InlineData("  promo  ", "promo")]
    [InlineData("black-friday", "black-friday")]
    [InlineData("black_friday", "black_friday")]
    [InlineData("a1", "a1")]
    public void TryNormalize_AsciiInput_LowercasesAndTrims(string raw, string expected)
    {
        Assert.True(SlugPolicy.TryNormalize(raw, out string slug));
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with.dot")]
    [InlineData("with?query")]
    [InlineData("with#fragment")]
    [InlineData("with%20escape")]
    [InlineData("emoji\U0001F600")]
    public void TryNormalize_DisallowedCharacters_AreRejected(string? raw)
    {
        Assert.False(SlugPolicy.TryNormalize(raw, out string slug));
        Assert.Equal(string.Empty, slug);
    }

    [Fact]
    public void TryNormalize_OverTheLengthLimit_IsRejected()
    {
        Assert.False(SlugPolicy.TryNormalize(new string('a', SlugPolicy.MaxLength + 1), out _));
        Assert.True(SlugPolicy.TryNormalize(new string('a', SlugPolicy.MaxLength), out _));
    }

    [Fact]
    public void TryNormalize_LoneSurrogate_IsRejectedWithoutThrowing()
    {
        Assert.False(SlugPolicy.TryNormalize("\ud800", out _));
    }

    [Fact]
    public void TryNormalize_IsIdempotent()
    {
        Assert.True(SlugPolicy.TryNormalize("Black-Friday_2026", out string once));
        Assert.True(SlugPolicy.TryNormalize(once, out string twice));
        Assert.Equal(once, twice);
    }

    // ---------------------------------------------------------------- reserved names

    [Fact]
    public void Reserved_ContainsTheRoutesTheEdgeOwns()
    {
        Assert.Contains(".well-known", SlugPolicy.Reserved);
        Assert.Contains("api", SlugPolicy.Reserved);
        Assert.Contains("healthz", SlugPolicy.Reserved);
        Assert.Contains("readyz", SlugPolicy.Reserved);
        Assert.Contains("abuse", SlugPolicy.Reserved);
        Assert.Contains("_dl", SlugPolicy.Reserved);
        Assert.Contains("favicon.ico", SlugPolicy.Reserved);
        Assert.Contains("robots.txt", SlugPolicy.Reserved);
        Assert.Contains("static", SlugPolicy.Reserved);
        Assert.Contains("assets", SlugPolicy.Reserved);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("healthz")]
    [InlineData("_dl")]
    [InlineData("robots.txt")]
    [InlineData(".well-known")]
    public void IsReserved_ReservedName_IsRecognisedCaseInsensitively(string slug)
    {
        Assert.True(SlugPolicy.IsReserved(slug));
    }

    [Theory]
    [InlineData("apis")]
    [InlineData("healthzz")]
    [InlineData("promo")]
    [InlineData("")]
    public void IsReserved_OrdinaryName_IsNotReserved(string slug)
    {
        Assert.False(SlugPolicy.IsReserved(slug));
    }

    [Fact]
    public void IsValidCustom_ReservedName_IsRefused()
    {
        Assert.False(SlugPolicy.IsValidCustom("api"));
        Assert.False(SlugPolicy.IsValidCustom("abuse"));
    }

    // ---------------------------------------------------------------- generated shape

    [Theory]
    [InlineData("dxlGgaI3", true)]
    [InlineData("00000000", true)]
    [InlineData("zzzzzzzz", true)]
    [InlineData("aB3xY9k2", true)]
    [InlineData("dxlGgaI", false)]      // seven characters
    [InlineData("dxlGgaI33", false)]    // nine characters
    [InlineData("dxlGga-3", false)]     // hyphen is not base62
    [InlineData("dxlGga_3", false)]     // underscore is not base62
    [InlineData("", false)]
    public void LooksGenerated_IsExactlyEightBase62Characters(string slug, bool expected)
    {
        Assert.Equal(expected, SlugPolicy.LooksGenerated(slug));
        Assert.Equal(8, SlugPolicy.GeneratedLength);
    }

    [Fact]
    public void IsValidCustom_SlugShapedLikeAGeneratedOne_IsRefused()
    {
        // Otherwise a customer could squat on the address space the slug generator will hand out.
        Assert.True(SlugPolicy.LooksGenerated("abcd1234"));
        Assert.False(SlugPolicy.IsValidCustom("abcd1234"));
    }

    [Theory]
    [InlineData("promo", true)]
    [InlineData("ab", false)]                       // shorter than MinCustomLength
    [InlineData("abc", true)]
    [InlineData("black-friday-2026", true)]
    [InlineData("nine-char", true)]                 // nine characters, distinguishable from generated
    [InlineData("with space", false)]
    [InlineData("Promo", false)]                    // uppercase must be normalised first
    [InlineData("", false)]
    public void IsValidCustom_AppliesTheLengthAndCharacterRules(string slug, bool expected)
    {
        Assert.Equal(expected, SlugPolicy.IsValidCustom(slug));
    }

    [Fact]
    public void IsValidCustom_AtTheLengthLimits_IsAccepted()
    {
        Assert.True(SlugPolicy.IsValidCustom(new string('a', SlugPolicy.MinCustomLength)));
        Assert.True(SlugPolicy.IsValidCustom(new string('a', SlugPolicy.MaxLength)));
        Assert.False(SlugPolicy.IsValidCustom(new string('a', SlugPolicy.MaxLength + 1)));
    }

    [Fact]
    public void Constants_MatchTheBindingContract()
    {
        Assert.Equal(8, SlugPolicy.GeneratedLength);
        Assert.Equal(3, SlugPolicy.MinCustomLength);
        Assert.Equal(64, SlugPolicy.MaxLength);
    }
}
