using System.Text;

namespace Dle.Domain.Primitives;

/// <summary>
/// Compares dotted version strings such as <c>"18"</c>, <c>"18.1"</c>, <c>"18.1.2"</c> or
/// <c>"3.4.1-beta"</c>. Missing components count as zero, so <c>"18"</c>, <c>"18.0"</c> and
/// <c>"18.0.0"</c> are equal.
/// </summary>
/// <remarks>
/// The comparison is numeric, never lexicographic: <c>"18.10"</c> is greater than <c>"18.9"</c>.
/// A pre-release or build suffix (everything from the first <c>'-'</c> or <c>'+'</c>) is ignored,
/// because client reported OS and application versions carry vendor specific suffixes that say
/// nothing about ordering. Nothing here throws: the input comes from an untrusted User-Agent or
/// SDK payload, so an unparsable component simply counts as zero.
/// </remarks>
public static class VersionComparer
{
    /// <summary>Longest input accepted by <see cref="TryNormalize"/>; anything longer is not a version.</summary>
    private const int MaxInputLength = 128;

    /// <summary>Characters that start a pre-release or build suffix.</summary>
    private static readonly char[] SuffixMarkers = ['-', '+'];

    /// <summary>
    /// Compares two versions.
    /// </summary>
    /// <param name="left">The left version, or <see langword="null"/>.</param>
    /// <param name="right">The right version, or <see langword="null"/>.</param>
    /// <returns>
    /// A negative number when <paramref name="left"/> sorts first, zero when the two are equal and
    /// a positive number otherwise. A <see langword="null"/> or blank version sorts below every real
    /// version, and two blank versions are equal.
    /// </returns>
    public static int Compare(string? left, string? right)
    {
        bool leftBlank = string.IsNullOrWhiteSpace(left);
        bool rightBlank = string.IsNullOrWhiteSpace(right);

        if (leftBlank || rightBlank)
        {
            return (leftBlank, rightBlank) switch
            {
                (true, true) => 0,
                (true, false) => -1,
                _ => 1,
            };
        }

        ReadOnlySpan<char> leftCore = Core(left!);
        ReadOnlySpan<char> rightCore = Core(right!);
        int leftPosition = 0;
        int rightPosition = 0;

        while (leftPosition < leftCore.Length || rightPosition < rightCore.Length)
        {
            ulong leftValue = ParseComponent(NextSegment(leftCore, ref leftPosition));
            ulong rightValue = ParseComponent(NextSegment(rightCore, ref rightPosition));

            if (leftValue != rightValue)
            {
                return leftValue < rightValue ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Rewrites a version into its canonical form: suffix removed, every component reduced to its
    /// numeric value with leading zeros dropped, components joined by <c>'.'</c>.
    /// </summary>
    /// <param name="raw">The version as reported by the client, or <see langword="null"/>.</param>
    /// <param name="normalized">
    /// The canonical form, for example <c>"17.4.1"</c> for <c>" 17.04.1-beta2 "</c>, or
    /// <see cref="string.Empty"/> when the value carries no version information.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the input is blank, longer than 128 characters, or contains no
    /// numeric component at all. The number of components is preserved, so <c>"18.0"</c> normalises
    /// to <c>"18.0"</c> and not to <c>"18"</c>; use <see cref="Compare"/> for equality.
    /// </returns>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxInputLength)
        {
            return false;
        }

        ReadOnlySpan<char> core = Core(raw);

        if (core.IsEmpty)
        {
            return false;
        }

        StringBuilder builder = new(core.Length);
        bool anyNumeric = false;
        int position = 0;

        while (position < core.Length)
        {
            ReadOnlySpan<char> component = NextSegment(core, ref position).Trim();

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            if (IsNumeric(component))
            {
                anyNumeric = true;
                builder.Append(ParseComponent(component).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append('0');
            }
        }

        if (!anyNumeric)
        {
            return false;
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>Strips surrounding whitespace and the pre-release or build suffix.</summary>
    private static ReadOnlySpan<char> Core(string raw)
    {
        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        int marker = span.IndexOfAny(SuffixMarkers);

        if (marker >= 0)
        {
            span = span[..marker];
        }

        return span.Trim();
    }

    /// <summary>
    /// Reads the component starting at <paramref name="position"/> and moves the position past the
    /// following separator. At the end of the input it returns an empty component and does not move,
    /// which is how a missing component becomes zero.
    /// </summary>
    private static ReadOnlySpan<char> NextSegment(ReadOnlySpan<char> text, ref int position)
    {
        if (position >= text.Length)
        {
            return [];
        }

        int start = position;
        int dot = text[start..].IndexOf('.');

        if (dot < 0)
        {
            position = text.Length;
            return text[start..];
        }

        position = start + dot + 1;
        return text.Slice(start, dot);
    }

    /// <summary>True when the component is non empty and consists only of ASCII digits.</summary>
    private static bool IsNumeric(ReadOnlySpan<char> component)
    {
        if (component.IsEmpty)
        {
            return false;
        }

        foreach (char c in component)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Value of a single component. An empty or non numeric component counts as zero; a component
    /// too large for <see cref="ulong"/> saturates instead of overflowing.
    /// </summary>
    private static ulong ParseComponent(ReadOnlySpan<char> component)
    {
        component = component.Trim();

        if (!IsNumeric(component))
        {
            return 0;
        }

        ulong value = 0;

        foreach (char c in component)
        {
            ulong digit = (ulong)(c - '0');

            if (value > (ulong.MaxValue - digit) / 10)
            {
                return ulong.MaxValue;
            }

            value = (value * 10) + digit;
        }

        return value;
    }
}
