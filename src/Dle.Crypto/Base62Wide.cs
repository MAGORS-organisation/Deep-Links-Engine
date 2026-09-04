namespace Dle.Crypto;

/// <summary>
/// Base62 for values wider than 64 bits, using the same alphabet as
/// <see cref="Base62"/> so that a click identifier and a slug are drawn from one character set.
/// </summary>
/// <remarks>
/// The shared kernel only encodes <see cref="ulong"/>, which is enough for the 47 bit slug space
/// but not for a click identifier: that one carries a timestamp, a sequence and a MAC, and the MAC
/// is what makes tampering detectable rather than merely improbable (T-05). Widening the kernel
/// helper for a single caller would put a 128 bit code path on the slug hot path, so the wide
/// variant lives here instead.
/// </remarks>
internal static class Base62Wide
{
    /// <summary>Numeric base.</summary>
    private const int Radix = 62;

    /// <summary>Digits needed for <see cref="UInt128.MaxValue"/>: 62^22 &gt; 2^128 &gt; 62^21.</summary>
    private const int MaxEncodedLength = 22;

    /// <summary>
    /// Encodes a value, most significant digit first, left padded with the zero digit.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <param name="length">Exact length of the result. The value must fit into it.</param>
    /// <returns>The base62 representation.</returns>
    internal static string Encode(UInt128 value, int length)
    {
        Span<char> digits = stackalloc char[MaxEncodedLength];
        int index = digits.Length;

        do
        {
            digits[--index] = Base62.Alphabet[(int)(value % Radix)];
            value /= Radix;
        }
        while (value != UInt128.Zero);

        ReadOnlySpan<char> encoded = digits[index..];
        int padding = length - encoded.Length;

        if (padding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The value does not fit into the requested length.");
        }

        Span<char> output = stackalloc char[length];
        output[..padding].Fill(Base62.Alphabet[0]);
        encoded.CopyTo(output[padding..]);

        return new string(output);
    }

    /// <summary>
    /// Decodes a base62 string. Never throws: the input is untrusted.
    /// </summary>
    /// <param name="text">The text to decode.</param>
    /// <param name="value">The decoded value, or zero on failure.</param>
    /// <returns><see langword="false"/> when the text is empty, holds a character outside the
    /// alphabet, or denotes a value above <see cref="UInt128.MaxValue"/>.</returns>
    internal static bool TryDecode(ReadOnlySpan<char> text, out UInt128 value)
    {
        value = UInt128.Zero;

        if (text.IsEmpty || text.Length > MaxEncodedLength)
        {
            return false;
        }

        UInt128 accumulator = UInt128.Zero;

        foreach (char c in text)
        {
            int digit = DigitOf(c);

            if (digit < 0)
            {
                return false;
            }

            if (accumulator > (UInt128.MaxValue - (UInt128)digit) / Radix)
            {
                return false;
            }

            accumulator = (accumulator * Radix) + (UInt128)digit;
        }

        value = accumulator;
        return true;
    }

    /// <summary>Maps a character to its digit value, or -1 when it is not part of the alphabet.</summary>
    private static int DigitOf(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        _ => -1,
    };
}
