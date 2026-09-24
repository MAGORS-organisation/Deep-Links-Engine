namespace Dle.Domain.Primitives;

/// <summary>
/// Base62 encoding and decoding of unsigned 64 bit values, used for the 47 bit slug space
/// (ADR-007) and for click identifiers.
/// </summary>
/// <remarks>
/// The encoding is big endian: the most significant digit comes first, so the lexicographic
/// order of two equally long encodings equals the numeric order of the values. The alphabet is
/// ordered digits, upper case, lower case, which makes <c>'0'</c> the zero digit used for padding.
/// </remarks>
public static class Base62
{
    /// <summary>The alphabet, in ascending digit order.</summary>
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Numeric base of the encoding.</summary>
    private const int Radix = 62;

    /// <summary>The digit with value zero; <see cref="Encode"/> pads with it.</summary>
    private const char Zero = '0';

    /// <summary>Digits needed for <see cref="ulong.MaxValue"/>: 62^11 &gt; 2^64 &gt; 62^10.</summary>
    private const int MaxEncodedLength = 11;

    /// <summary>Longest padded result that is still built on the stack.</summary>
    private const int StackLimit = 128;

    /// <summary>
    /// Encodes a value, most significant digit first.
    /// </summary>
    /// <param name="value">The value to encode. Zero encodes to <c>"0"</c>.</param>
    /// <param name="minLength">
    /// Minimum length of the result. Shorter encodings are left padded with <c>'0'</c>, which does not
    /// change the value; a value that needs more digits than this is never truncated. Slug generation
    /// passes <see cref="SlugPolicy.GeneratedLength"/> here.
    /// </param>
    /// <returns>The base62 representation of <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minLength"/> is negative.</exception>
    public static string Encode(ulong value, int minLength = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minLength);

        Span<char> digits = stackalloc char[MaxEncodedLength];
        int index = digits.Length;

        do
        {
            digits[--index] = Alphabet[(int)(value % Radix)];
            value /= Radix;
        }
        while (value != 0);

        ReadOnlySpan<char> encoded = digits[index..];
        int padding = minLength - encoded.Length;

        if (padding <= 0)
        {
            return new string(encoded);
        }

        int total = encoded.Length + padding;
        Span<char> output = total <= StackLimit ? stackalloc char[StackLimit] : new char[total];
        output = output[..total];
        output[..padding].Fill(Zero);
        encoded.CopyTo(output[padding..]);

        return new string(output);
    }

    /// <summary>
    /// Decodes a base62 string. The method never throws.
    /// </summary>
    /// <param name="text">
    /// The text to decode. Leading <c>'0'</c> digits are accepted and ignored, so a padded encoding
    /// round trips to the value it was produced from.
    /// </param>
    /// <param name="value">The decoded value, or zero when the text is not decodable.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="text"/> is empty, contains a character outside
    /// <see cref="Alphabet"/>, or denotes a value larger than <see cref="ulong.MaxValue"/>.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0;

        if (text.IsEmpty)
        {
            return false;
        }

        ulong accumulator = 0;

        foreach (char c in text)
        {
            int digit = DigitOf(c);

            if (digit < 0)
            {
                return false;
            }

            // accumulator * 62 + digit must stay below ulong.MaxValue.
            if (accumulator > (ulong.MaxValue - (ulong)digit) / Radix)
            {
                return false;
            }

            accumulator = (accumulator * Radix) + (ulong)digit;
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
