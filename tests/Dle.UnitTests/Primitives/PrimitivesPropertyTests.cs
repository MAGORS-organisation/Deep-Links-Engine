using System.Globalization;
using CsCheck;
using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// Property-based coverage for the primitives (section D.4: "property based, mainly for the routing
/// engine and URL normalisation"). Example based tests pin the cases somebody thought of; these pin
/// the laws, which is where the parsers actually break.
/// </summary>
public sealed class PrimitivesPropertyTests
{
    [Fact]
    public void Base62_EncodeThenDecode_IsAnInvolution()
    {
        Gen.ULong.Sample(value =>
        {
            string encoded = Base62.Encode(value);

            Assert.True(Base62.TryDecode(encoded, out ulong decoded), $"'{encoded}' did not decode.");
            Assert.Equal(value, decoded);
        });
    }

    [Fact]
    public void Base62_Encode_OnlyEverEmitsAlphabetCharacters()
    {
        Gen.ULong.Sample(value =>
        {
            foreach (char c in Base62.Encode(value))
            {
                Assert.Contains(c, Base62.Alphabet);
            }
        });
    }

    [Fact]
    public void Base62_EncodeIsOrderPreserving()
    {
        Gen.Select(Gen.ULong, Gen.ULong).Sample(pair =>
        {
            (ulong left, ulong right) = pair;
            string leftText = Base62.Encode(left, minLength: 11);
            string rightText = Base62.Encode(right, minLength: 11);

            Assert.Equal(
                Math.Sign(left.CompareTo(right)),
                Math.Sign(string.CompareOrdinal(leftText, rightText)));
        });
    }

    [Fact]
    public void Base62_MinLengthPadding_NeverChangesTheDecodedValue()
    {
        Gen.Select(Gen.ULong, Gen.Int[1, 20]).Sample(pair =>
        {
            (ulong value, int minLength) = pair;
            string encoded = Base62.Encode(value, minLength);

            Assert.True(encoded.Length >= minLength);
            Assert.True(Base62.TryDecode(encoded, out ulong decoded));
            Assert.Equal(value, decoded);
        });
    }

    [Fact]
    public void Base62_TryDecode_NeverThrowsOnArbitraryText()
    {
        Gen.String.Sample(text =>
        {
            bool decoded = Base62.TryDecode(text, out ulong value);

            // A successful decode must round trip; a failed one must leave the output at zero.
            if (decoded)
            {
                Assert.True(Base62.TryDecode(Base62.Encode(value), out ulong again));
                Assert.Equal(value, again);
            }
            else
            {
                Assert.Equal(0UL, value);
            }
        });
    }

    /// <summary>
    /// Slug-shaped input: the allowed alphabet, plus uppercase, plus whitespace, plus a handful of
    /// Cyrillic confusables, so that both the accepting and the rejecting path are exercised rather
    /// than every sample being thrown out by the character filter.
    /// </summary>
    private static readonly Gen<string> SlugishGen =
        Gen.String["abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-. .аеорсхѕӏ"];

    /// <summary>Host-shaped input: letters, digits, separators, and the characters that carry meaning.</summary>
    private static readonly Gen<string> HostishGen =
        Gen.String["abcdefghijklmnopqrstuvwxyzABCDEFGHJKLMNOPQRSTUVWXYZ0123456789.-:/@wwwáč"];

    [Fact]
    public void SlugPolicy_TryNormalize_IsIdempotent()
    {
        SlugishGen.Sample(raw =>
        {
            if (!SlugPolicy.TryNormalize(raw, out string once))
            {
                return;
            }

            Assert.True(SlugPolicy.TryNormalize(once, out string twice), $"'{once}' stopped normalising.");
            Assert.Equal(once, twice);
        });
    }

    [Fact]
    public void SlugPolicy_TryNormalize_NeverThrowsAndNeverEmitsADisallowedCharacter()
    {
        Gen.String.Sample(raw =>
        {
            if (!SlugPolicy.TryNormalize(raw, out string slug))
            {
                Assert.Equal(string.Empty, slug);
                return;
            }

            Assert.InRange(slug.Length, 1, SlugPolicy.MaxLength);
            foreach (char c in slug)
            {
                Assert.True(
                    char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-',
                    $"'{slug}' contains the disallowed character U+{(int)c:X4}.");
            }
        });
    }

    [Fact]
    public void SlugPolicy_AValidCustomSlug_SurvivesNormalisationUnchanged()
    {
        SlugishGen.Sample(raw =>
        {
            if (!SlugPolicy.TryNormalize(raw, out string slug) || !SlugPolicy.IsValidCustom(slug))
            {
                return;
            }

            Assert.True(SlugPolicy.TryNormalize(slug, out string again));
            Assert.Equal(slug, again);
            Assert.False(SlugPolicy.IsReserved(slug));
            Assert.False(SlugPolicy.LooksGenerated(slug));
        });
    }

    [Fact]
    public void HostNormalizer_TryNormalize_NeverThrowsOnAnyInput()
    {
        Gen.String.Sample(raw =>
        {
            if (!HostNormalizer.TryNormalize(raw, out string host))
            {
                Assert.Equal(string.Empty, host);
                return;
            }

            Assert.InRange(host.Length, 1, HostNormalizer.MaxLength);
            foreach (char c in host)
            {
                Assert.True(
                    char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-',
                    $"'{host}' contains the disallowed character U+{(int)c:X4}.");
            }
        });
    }

    [Fact]
    public void HostNormalizer_TryNormalize_IsIdempotent()
    {
        HostishGen.Sample(raw =>
        {
            if (!HostNormalizer.TryNormalize(raw, out string once))
            {
                return;
            }

            Assert.True(HostNormalizer.TryNormalize(once, out string twice), $"'{once}' stopped normalising.");
            Assert.Equal(once, twice);
        });
    }

    [Fact]
    public void VersionComparer_Compare_IsATotalOrder()
    {
        Gen.Select(VersionGen, VersionGen, VersionGen).Sample(triple =>
        {
            (string a, string b, string c) = triple;

            int ab = Math.Sign(VersionComparer.Compare(a, b));
            int ba = Math.Sign(VersionComparer.Compare(b, a));

            Assert.Equal(-ab, ba);
            Assert.Equal(0, Math.Sign(VersionComparer.Compare(a, a)));

            if (ab <= 0 && Math.Sign(VersionComparer.Compare(b, c)) <= 0)
            {
                Assert.True(Math.Sign(VersionComparer.Compare(a, c)) <= 0, $"{a} <= {b} <= {c} was not transitive.");
            }
        });
    }

    [Fact]
    public void VersionComparer_TryNormalize_PreservesEveryComparison()
    {
        Gen.Select(VersionGen, VersionGen).Sample(pair =>
        {
            (string left, string right) = pair;
            if (!VersionComparer.TryNormalize(left, out string normalizedLeft)
                || !VersionComparer.TryNormalize(right, out string normalizedRight))
            {
                return;
            }

            Assert.Equal(
                Math.Sign(VersionComparer.Compare(left, right)),
                Math.Sign(VersionComparer.Compare(normalizedLeft, normalizedRight)));
        });
    }

    [Fact]
    public void VersionComparer_TryNormalize_NeverThrows()
    {
        Gen.String.Sample(raw =>
        {
            if (VersionComparer.TryNormalize(raw, out string normalized))
            {
                Assert.NotEqual(string.Empty, normalized);
                Assert.True(VersionComparer.TryNormalize(normalized, out string again));
                Assert.Equal(normalized, again);
            }
            else
            {
                Assert.Equal(string.Empty, normalized);
            }
        });
    }

    /// <summary>Version-shaped strings: a few numeric components plus an optional prerelease tail.</summary>
    private static readonly Gen<string> VersionGen =
        Gen.Select(Gen.Int[0, 30], Gen.Int[0, 30], Gen.Int[0, 30], Gen.Int[0, 3])
            .Select(t => t.Item4 switch
            {
                0 => t.Item1.ToString(CultureInfo.InvariantCulture),
                1 => FormattableString.Invariant($"{t.Item1}.{t.Item2}"),
                2 => FormattableString.Invariant($"{t.Item1}.{t.Item2}.{t.Item3}"),
                _ => FormattableString.Invariant($"{t.Item1}.{t.Item2}.{t.Item3}-beta.{t.Item3}"),
            });
}
