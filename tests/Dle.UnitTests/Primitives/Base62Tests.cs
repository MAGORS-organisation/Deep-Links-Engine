using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// ADR-007: the slug is base62 of a keyed permutation of a sequence. If encode/decode is not an
/// exact involution over the whole 64-bit range, slugs collide or become unrecoverable.
/// </summary>
public sealed class Base62Tests
{
    [Fact]
    public void Alphabet_IsTheDocumentedDigitsThenUpperThenLowerOrdering()
    {
        Assert.Equal("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", Base62.Alphabet);
        Assert.Equal(62, Base62.Alphabet.Length);
    }

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(1UL, "1")]
    [InlineData(9UL, "9")]
    [InlineData(10UL, "A")]
    [InlineData(35UL, "Z")]
    [InlineData(36UL, "a")]
    [InlineData(61UL, "z")]
    [InlineData(62UL, "10")]
    [InlineData(3843UL, "zz")]
    [InlineData(3844UL, "100")]
    public void Encode_SmallValues_MatchThePositionalNotation(ulong value, string expected)
    {
        Assert.Equal(expected, Base62.Encode(value));
    }

    [Fact]
    public void Encode_MaxValue_ProducesElevenDigitsThatDecodeBack()
    {
        string encoded = Base62.Encode(ulong.MaxValue);

        Assert.Equal(11, encoded.Length);
        Assert.True(Base62.TryDecode(encoded, out ulong decoded));
        Assert.Equal(ulong.MaxValue, decoded);
    }

    [Fact]
    public void Encode_WithMinLength_LeftPadsWithZeroAndStillDecodes()
    {
        string encoded = Base62.Encode(1, minLength: 8);

        Assert.Equal("00000001", encoded);
        Assert.True(Base62.TryDecode(encoded, out ulong decoded));
        Assert.Equal(1UL, decoded);
    }

    [Fact]
    public void Encode_MinLengthShorterThanTheValue_DoesNotTruncate()
    {
        Assert.Equal("100", Base62.Encode(3844, minLength: 2));
    }

    [Fact]
    public void Encode_MinLengthLongerThanTheStackBuffer_StillPads()
    {
        string encoded = Base62.Encode(1, minLength: 200);

        Assert.Equal(200, encoded.Length);
        Assert.EndsWith("1", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_NegativeMinLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base62.Encode(1, minLength: -1));
    }

    [Fact]
    public void TryDecode_EmptySpan_Fails()
    {
        Assert.False(Base62.TryDecode(string.Empty, out ulong value));
        Assert.Equal(0UL, value);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1 2")]
    [InlineData("ab_cd")]
    [InlineData("héllo")]
    [InlineData("+")]
    [InlineData("/")]
    public void TryDecode_NonAlphabetCharacter_Fails(string text)
    {
        Assert.False(Base62.TryDecode(text, out _));
    }

    [Fact]
    public void TryDecode_ValueBeyondUInt64_FailsRatherThanWrappingAround()
    {
        // "LygHa16AHYF" is ulong.MaxValue; the next value in base62 order overflows.
        Assert.True(Base62.TryDecode("LygHa16AHYF", out ulong max));
        Assert.Equal(ulong.MaxValue, max);

        Assert.False(Base62.TryDecode("LygHa16AHYG", out _));
        Assert.False(Base62.TryDecode("zzzzzzzzzzzz", out _));
        Assert.False(Base62.TryDecode("100000000000", out _));
    }

    [Fact]
    public void TryDecode_LeadingZeroes_AreIgnored()
    {
        Assert.True(Base62.TryDecode("0000000000000000000001", out ulong value));
        Assert.Equal(1UL, value);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(61UL)]
    [InlineData(62UL)]
    [InlineData(140737488355327UL)] // 2^47 - 1, the Feistel domain of ADR-007
    [InlineData(140737488355328UL)]
    [InlineData(9223372036854775808UL)]
    [InlineData(18446744073709551614UL)]
    [InlineData(ulong.MaxValue)]
    public void EncodeThenDecode_RoundTripsAtTheBoundaries(ulong value)
    {
        Assert.True(Base62.TryDecode(Base62.Encode(value), out ulong decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Encode_TwoDifferentValues_NeverShareAnEncoding()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong value = 0; value < 5000; value++)
        {
            Assert.True(seen.Add(Base62.Encode(value)));
        }
    }
}
