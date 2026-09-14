using Dle.Crypto;
using Dle.Domain.Crypto;
using Dle.Domain.Primitives;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// The click identifier is public — it travels in the Play install referrer — so it is
/// authenticated rather than secret (§B.6.3). Two properties matter: the embedded instant must
/// survive the round trip to the millisecond, because the attribution query prunes partitions with
/// it; and any mutation must fail loudly, because a mutated identifier that still decoded would
/// point the lookup at a plausible but wrong instant and silently find nothing (T-05, TC-167).
/// </summary>
public sealed class ClickIdCodecTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 15, 9, 26, 535, TimeSpan.Zero);

    private static ClickIdCodec Codec(string label = "primary") =>
        new(CryptoTestKeys.Of(label + ":permutation"), CryptoTestKeys.Of(label + ":mac"));

    [Fact]
    public void New_ProducesSeventeenBase62Characters()
    {
        string clickId = Codec().New(Instant);

        Assert.Equal(ClickIdCodec.Length, clickId.Length);
        Assert.Equal(17, clickId.Length);

        foreach (char c in clickId)
        {
            Assert.True(Base62.Alphabet.Contains(c, StringComparison.Ordinal), $"'{c}' is not a base62 digit");
        }
    }

    [Fact]
    public void TryDecode_RoundTripsTheInstantToTheMillisecond()
    {
        ClickIdCodec codec = Codec();

        foreach (DateTimeOffset when in new[]
        {
            ClickIdCodec.TimestampEpoch,
            ClickIdCodec.TimestampEpoch.AddMilliseconds(1),
            Instant,
            Instant.AddMilliseconds(999),
            new DateTimeOffset(2031, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
        })
        {
            string clickId = codec.New(when);

            Assert.True(codec.TryDecode(clickId, out DateTimeOffset decoded, out _));
            Assert.Equal(when.ToUniversalTime(), decoded);
        }
    }

    [Fact]
    public void TryDecode_PreservesTheInstantAcrossTimeZoneOffsets()
    {
        ClickIdCodec codec = Codec();

        var withOffset = new DateTimeOffset(2026, 6, 1, 12, 0, 0, 250, TimeSpan.FromHours(2));

        Assert.True(codec.TryDecode(codec.New(withOffset), out DateTimeOffset decoded, out _));
        Assert.Equal(withOffset.ToUniversalTime(), decoded);
        Assert.Equal(TimeSpan.Zero, decoded.Offset);
    }

    [Fact]
    public void TryDecode_ReturnsTheSequenceThatWasMinted()
    {
        ClickIdCodec codec = Codec();

        string first = codec.New(Instant);
        string second = codec.New(Instant);

        Assert.True(codec.TryDecode(first, out _, out long firstSequence));
        Assert.True(codec.TryDecode(second, out _, out long secondSequence));

        // Two identifiers minted in the same millisecond differ only in the sequence, and the
        // counter advances by one, which is what keeps them distinct without a clock read.
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstSequence, secondSequence);
        Assert.Equal(firstSequence + 1, secondSequence);

        // Decoding is a pure function of the identifier.
        Assert.True(codec.TryDecode(first, out _, out long again));
        Assert.Equal(firstSequence, again);
    }

    /// <summary>
    /// T-05 / TC-167. Every single-character mutation of a valid identifier — 17 positions times
    /// the 61 other base62 digits — must be refused. This is the whole reason the identifier is a
    /// MAC-ed token rather than a raw counter.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-167")]
    public void TryDecode_EverySingleCharacterMutation_IsRejected()
    {
        ClickIdCodec codec = Codec();

        string clickId = codec.New(Instant);
        Assert.True(codec.TryDecode(clickId, out _, out _));

        int mutations = 0;

        for (int position = 0; position < clickId.Length; position++)
        {
            foreach (char replacement in Base62.Alphabet)
            {
                if (replacement == clickId[position])
                {
                    continue;
                }

                char[] mutated = clickId.ToCharArray();
                mutated[position] = replacement;

                string candidate = new(mutated);
                mutations++;

                Assert.False(
                    codec.TryDecode(candidate, out _, out _),
                    $"mutation at position {position} to '{replacement}' was accepted: {candidate}");
            }
        }

        Assert.Equal(17 * 61, mutations);
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    public void TryDecode_IdentifierMintedWithAnotherKey_IsRejected()
    {
        string foreign = Codec("other").New(Instant);

        Assert.False(Codec().TryDecode(foreign, out _, out _));
    }

    [Fact]
    public void TryDecode_IdentifierWithTheSamePermutationButAnotherMacKey_IsRejected()
    {
        var minted = new ClickIdCodec(CryptoTestKeys.Of("primary:permutation"), CryptoTestKeys.Of("mac-a"));
        var reader = new ClickIdCodec(CryptoTestKeys.Of("primary:permutation"), CryptoTestKeys.Of("mac-b"));

        Assert.False(reader.TryDecode(minted.New(Instant), out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooshort")]
    [InlineData("0000000000000000000000000000000000")]
    [InlineData("abcdefghijklmnop!")]
    [InlineData("abcdefghijklmno p")]
    [InlineData("zzzzzzzzzzzzzzzzz")]
    [InlineData("-----------------")]
    public void TryDecode_MalformedInput_ReturnsFalseWithoutThrowing(string candidate)
    {
        ClickIdCodec codec = Codec();

        Assert.False(codec.TryDecode(candidate, out DateTimeOffset when, out long sequence));
        Assert.Equal(default, when);
        Assert.Equal(0L, sequence);
    }

    [Fact]
    public void New_InstantBeforeTheEpoch_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec().New(ClickIdCodec.TimestampEpoch.AddMilliseconds(-1)));

    [Fact]
    public void New_InstantBeyondTheTimestampField_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec().New(ClickIdCodec.TimestampEpoch.AddYears(200)));

    [Fact]
    public void Constructor_ShortMacKey_Throws() =>
        Assert.Throws<ArgumentException>(() => new ClickIdCodec(CryptoTestKeys.Primary, new byte[15]));

    [Fact]
    public void Codec_IsTheContractedInterface() =>
        Assert.IsAssignableFrom<IClickIdCodec>(Codec());

    [Fact]
    public void MacComparison_UsesFixedTimeEquals() =>
        Assert.True(
            ConstantTimeAssertion.CallsFixedTimeEquals(typeof(ClickIdCodec), "MacMatches"),
            "ClickIdCodec.MacMatches must compare the presented MAC with CryptographicOperations.FixedTimeEquals (T-17, S-03).");
}
