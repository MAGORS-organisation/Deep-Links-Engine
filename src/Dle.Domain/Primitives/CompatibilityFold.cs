using System.Text;

namespace Dle.Domain.Primitives;

/// <summary>
/// The part of Unicode NFKC that <see cref="SlugPolicy"/> depends on, implemented without ICU.
/// </summary>
/// <remarks>
/// <para>
/// The product builds with <c>InvariantGlobalization</c>, and in globalization-invariant mode
/// <see cref="string.Normalize(NormalizationForm)"/> is a silent no-op: it returns its input
/// unchanged and <see cref="string.IsNormalized(NormalizationForm)"/> answers <see langword="true"/>
/// for text that is not normalised at all. The NFKC step that SHARED-KERNEL section 1 requires of
/// <see cref="SlugPolicy.TryNormalize"/> therefore did not run, and compatibility spellings such as
/// full width <c>PROMO</c> were refused instead of folded.
/// </para>
/// <para>
/// This table restores that step deterministically. It contains exactly those code points whose NFKC
/// image lies entirely inside the slug alphabet <c>[0-9A-Za-z_-]</c> — every other compatibility
/// character folds to something the allowlist refuses anyway, so omitting it cannot change a verdict.
/// Confusables are deliberately absent: NFKC does not map Cyrillic <c>а</c> (U+0430) onto Latin
/// <c>a</c>, and neither does this table, which is what keeps one tenant out of another's address
/// space (TC-109).
/// </para>
/// <para>
/// Applying this fold after <see cref="string.Normalize(NormalizationForm)"/> is safe with or without
/// ICU: where ICU is present the runtime has already removed every code point in this table, so the
/// fold is a no-op and the two configurations agree. The table is generated from the Unicode data of
/// the .NET 10 ICU build; Unicode's normalisation stability policy guarantees these mappings never
/// change.
/// </para>
/// </remarks>
internal static class CompatibilityFold
{
    /// <summary>Single character mappings, run length encoded as (first, last, image of first).</summary>
    private static readonly (int Start, int End, char To)[] Runs =
    [
        (0x00AA, 0x00AA, 'a'),
        (0x00B2, 0x00B3, '2'),
        (0x00B9, 0x00B9, '1'),
        (0x00BA, 0x00BA, 'o'),
        (0x017F, 0x017F, 's'),
        (0x02B0, 0x02B0, 'h'),
        (0x02B2, 0x02B2, 'j'),
        (0x02B3, 0x02B3, 'r'),
        (0x02B7, 0x02B7, 'w'),
        (0x02B8, 0x02B8, 'y'),
        (0x02E1, 0x02E1, 'l'),
        (0x02E2, 0x02E2, 's'),
        (0x02E3, 0x02E3, 'x'),
        (0x1D2C, 0x1D2C, 'A'),
        (0x1D2E, 0x1D2E, 'B'),
        (0x1D30, 0x1D31, 'D'),
        (0x1D33, 0x1D3A, 'G'),
        (0x1D3C, 0x1D3C, 'O'),
        (0x1D3E, 0x1D3E, 'P'),
        (0x1D3F, 0x1D3F, 'R'),
        (0x1D40, 0x1D41, 'T'),
        (0x1D42, 0x1D42, 'W'),
        (0x1D43, 0x1D43, 'a'),
        (0x1D47, 0x1D47, 'b'),
        (0x1D48, 0x1D49, 'd'),
        (0x1D4D, 0x1D4D, 'g'),
        (0x1D4F, 0x1D4F, 'k'),
        (0x1D50, 0x1D50, 'm'),
        (0x1D52, 0x1D52, 'o'),
        (0x1D56, 0x1D56, 'p'),
        (0x1D57, 0x1D58, 't'),
        (0x1D5B, 0x1D5B, 'v'),
        (0x1D62, 0x1D62, 'i'),
        (0x1D63, 0x1D63, 'r'),
        (0x1D64, 0x1D65, 'u'),
        (0x1D9C, 0x1D9C, 'c'),
        (0x1DA0, 0x1DA0, 'f'),
        (0x1DBB, 0x1DBB, 'z'),
        (0x2070, 0x2070, '0'),
        (0x2071, 0x2071, 'i'),
        (0x2074, 0x2079, '4'),
        (0x207F, 0x207F, 'n'),
        (0x2080, 0x2089, '0'),
        (0x2090, 0x2090, 'a'),
        (0x2091, 0x2091, 'e'),
        (0x2092, 0x2092, 'o'),
        (0x2093, 0x2093, 'x'),
        (0x2095, 0x2095, 'h'),
        (0x2096, 0x2099, 'k'),
        (0x209A, 0x209A, 'p'),
        (0x209B, 0x209C, 's'),
        (0x2102, 0x2102, 'C'),
        (0x210A, 0x210A, 'g'),
        (0x210B, 0x210B, 'H'),
        (0x210C, 0x210C, 'H'),
        (0x210D, 0x210D, 'H'),
        (0x210E, 0x210E, 'h'),
        (0x2110, 0x2110, 'I'),
        (0x2111, 0x2111, 'I'),
        (0x2112, 0x2112, 'L'),
        (0x2113, 0x2113, 'l'),
        (0x2115, 0x2115, 'N'),
        (0x2119, 0x211B, 'P'),
        (0x211C, 0x211C, 'R'),
        (0x211D, 0x211D, 'R'),
        (0x2124, 0x2124, 'Z'),
        (0x2128, 0x2128, 'Z'),
        (0x212A, 0x212A, 'K'),
        (0x212C, 0x212D, 'B'),
        (0x212F, 0x212F, 'e'),
        (0x2130, 0x2131, 'E'),
        (0x2133, 0x2133, 'M'),
        (0x2134, 0x2134, 'o'),
        (0x2139, 0x2139, 'i'),
        (0x2145, 0x2145, 'D'),
        (0x2146, 0x2147, 'd'),
        (0x2148, 0x2149, 'i'),
        (0x2160, 0x2160, 'I'),
        (0x2164, 0x2164, 'V'),
        (0x2169, 0x2169, 'X'),
        (0x216C, 0x216C, 'L'),
        (0x216D, 0x216E, 'C'),
        (0x216F, 0x216F, 'M'),
        (0x2170, 0x2170, 'i'),
        (0x2174, 0x2174, 'v'),
        (0x2179, 0x2179, 'x'),
        (0x217C, 0x217C, 'l'),
        (0x217D, 0x217E, 'c'),
        (0x217F, 0x217F, 'm'),
        (0x2460, 0x2468, '1'),
        (0x24B6, 0x24CF, 'A'),
        (0x24D0, 0x24E9, 'a'),
        (0x24EA, 0x24EA, '0'),
        (0x2C7C, 0x2C7C, 'j'),
        (0x2C7D, 0x2C7D, 'V'),
        (0xA7F2, 0xA7F2, 'C'),
        (0xA7F3, 0xA7F3, 'F'),
        (0xA7F4, 0xA7F4, 'Q'),
        (0xFE33, 0xFE33, '_'),
        (0xFE34, 0xFE34, '_'),
        (0xFE4D, 0xFE4D, '_'),
        (0xFE4E, 0xFE4E, '_'),
        (0xFE4F, 0xFE4F, '_'),
        (0xFE63, 0xFE63, '-'),
        (0xFF0D, 0xFF0D, '-'),
        (0xFF10, 0xFF19, '0'),
        (0xFF21, 0xFF3A, 'A'),
        (0xFF3F, 0xFF3F, '_'),
        (0xFF41, 0xFF5A, 'a'),
        (0x107A5, 0x107A5, 'q'),
        (0x1D400, 0x1D419, 'A'),
        (0x1D41A, 0x1D433, 'a'),
        (0x1D434, 0x1D44D, 'A'),
        (0x1D44E, 0x1D454, 'a'),
        (0x1D456, 0x1D467, 'i'),
        (0x1D468, 0x1D481, 'A'),
        (0x1D482, 0x1D49B, 'a'),
        (0x1D49C, 0x1D49C, 'A'),
        (0x1D49E, 0x1D49F, 'C'),
        (0x1D4A2, 0x1D4A2, 'G'),
        (0x1D4A5, 0x1D4A6, 'J'),
        (0x1D4A9, 0x1D4AC, 'N'),
        (0x1D4AE, 0x1D4B5, 'S'),
        (0x1D4B6, 0x1D4B9, 'a'),
        (0x1D4BB, 0x1D4BB, 'f'),
        (0x1D4BD, 0x1D4C3, 'h'),
        (0x1D4C5, 0x1D4CF, 'p'),
        (0x1D4D0, 0x1D4E9, 'A'),
        (0x1D4EA, 0x1D503, 'a'),
        (0x1D504, 0x1D505, 'A'),
        (0x1D507, 0x1D50A, 'D'),
        (0x1D50D, 0x1D514, 'J'),
        (0x1D516, 0x1D51C, 'S'),
        (0x1D51E, 0x1D537, 'a'),
        (0x1D538, 0x1D539, 'A'),
        (0x1D53B, 0x1D53E, 'D'),
        (0x1D540, 0x1D544, 'I'),
        (0x1D546, 0x1D546, 'O'),
        (0x1D54A, 0x1D550, 'S'),
        (0x1D552, 0x1D56B, 'a'),
        (0x1D56C, 0x1D585, 'A'),
        (0x1D586, 0x1D59F, 'a'),
        (0x1D5A0, 0x1D5B9, 'A'),
        (0x1D5BA, 0x1D5D3, 'a'),
        (0x1D5D4, 0x1D5ED, 'A'),
        (0x1D5EE, 0x1D607, 'a'),
        (0x1D608, 0x1D621, 'A'),
        (0x1D622, 0x1D63B, 'a'),
        (0x1D63C, 0x1D655, 'A'),
        (0x1D656, 0x1D66F, 'a'),
        (0x1D670, 0x1D689, 'A'),
        (0x1D68A, 0x1D6A3, 'a'),
        (0x1D7CE, 0x1D7D7, '0'),
        (0x1D7D8, 0x1D7E1, '0'),
        (0x1D7E2, 0x1D7EB, '0'),
        (0x1D7EC, 0x1D7F5, '0'),
        (0x1D7F6, 0x1D7FF, '0'),
        (0x1F12B, 0x1F12B, 'C'),
        (0x1F12C, 0x1F12C, 'R'),
        (0x1F130, 0x1F149, 'A'),
        (0x1FBF0, 0x1FBF9, '0'),
    ];

    /// <summary>Code points whose NFKC image is more than one character, ordered by code point.</summary>
    private static readonly (int CodePoint, string To)[] Expansions =
    [
        (0x0132, "IJ"),
        (0x0133, "ij"),
        (0x01C7, "LJ"),
        (0x01C8, "Lj"),
        (0x01C9, "lj"),
        (0x01CA, "NJ"),
        (0x01CB, "Nj"),
        (0x01CC, "nj"),
        (0x01F1, "DZ"),
        (0x01F2, "Dz"),
        (0x01F3, "dz"),
        (0x20A8, "Rs"),
        (0x2116, "No"),
        (0x2120, "SM"),
        (0x2121, "TEL"),
        (0x2122, "TM"),
        (0x213B, "FAX"),
        (0x2161, "II"),
        (0x2162, "III"),
        (0x2163, "IV"),
        (0x2165, "VI"),
        (0x2166, "VII"),
        (0x2167, "VIII"),
        (0x2168, "IX"),
        (0x216A, "XI"),
        (0x216B, "XII"),
        (0x2171, "ii"),
        (0x2172, "iii"),
        (0x2173, "iv"),
        (0x2175, "vi"),
        (0x2176, "vii"),
        (0x2177, "viii"),
        (0x2178, "ix"),
        (0x217A, "xi"),
        (0x217B, "xii"),
        (0x2469, "10"),
        (0x246A, "11"),
        (0x246B, "12"),
        (0x246C, "13"),
        (0x246D, "14"),
        (0x246E, "15"),
        (0x246F, "16"),
        (0x2470, "17"),
        (0x2471, "18"),
        (0x2472, "19"),
        (0x2473, "20"),
        (0x3250, "PTE"),
        (0x3251, "21"),
        (0x3252, "22"),
        (0x3253, "23"),
        (0x3254, "24"),
        (0x3255, "25"),
        (0x3256, "26"),
        (0x3257, "27"),
        (0x3258, "28"),
        (0x3259, "29"),
        (0x325A, "30"),
        (0x325B, "31"),
        (0x325C, "32"),
        (0x325D, "33"),
        (0x325E, "34"),
        (0x325F, "35"),
        (0x32B1, "36"),
        (0x32B2, "37"),
        (0x32B3, "38"),
        (0x32B4, "39"),
        (0x32B5, "40"),
        (0x32B6, "41"),
        (0x32B7, "42"),
        (0x32B8, "43"),
        (0x32B9, "44"),
        (0x32BA, "45"),
        (0x32BB, "46"),
        (0x32BC, "47"),
        (0x32BD, "48"),
        (0x32BE, "49"),
        (0x32BF, "50"),
        (0x32CC, "Hg"),
        (0x32CD, "erg"),
        (0x32CE, "eV"),
        (0x32CF, "LTD"),
        (0x3371, "hPa"),
        (0x3372, "da"),
        (0x3373, "AU"),
        (0x3374, "bar"),
        (0x3375, "oV"),
        (0x3376, "pc"),
        (0x3377, "dm"),
        (0x3378, "dm2"),
        (0x3379, "dm3"),
        (0x337A, "IU"),
        (0x3380, "pA"),
        (0x3381, "nA"),
        (0x3383, "mA"),
        (0x3384, "kA"),
        (0x3385, "KB"),
        (0x3386, "MB"),
        (0x3387, "GB"),
        (0x3388, "cal"),
        (0x3389, "kcal"),
        (0x338A, "pF"),
        (0x338B, "nF"),
        (0x338E, "mg"),
        (0x338F, "kg"),
        (0x3390, "Hz"),
        (0x3391, "kHz"),
        (0x3392, "MHz"),
        (0x3393, "GHz"),
        (0x3394, "THz"),
        (0x3396, "ml"),
        (0x3397, "dl"),
        (0x3398, "kl"),
        (0x3399, "fm"),
        (0x339A, "nm"),
        (0x339C, "mm"),
        (0x339D, "cm"),
        (0x339E, "km"),
        (0x339F, "mm2"),
        (0x33A0, "cm2"),
        (0x33A1, "m2"),
        (0x33A2, "km2"),
        (0x33A3, "mm3"),
        (0x33A4, "cm3"),
        (0x33A5, "m3"),
        (0x33A6, "km3"),
        (0x33A9, "Pa"),
        (0x33AA, "kPa"),
        (0x33AB, "MPa"),
        (0x33AC, "GPa"),
        (0x33AD, "rad"),
        (0x33B0, "ps"),
        (0x33B1, "ns"),
        (0x33B3, "ms"),
        (0x33B4, "pV"),
        (0x33B5, "nV"),
        (0x33B7, "mV"),
        (0x33B8, "kV"),
        (0x33B9, "MV"),
        (0x33BA, "pW"),
        (0x33BB, "nW"),
        (0x33BD, "mW"),
        (0x33BE, "kW"),
        (0x33BF, "MW"),
        (0x33C3, "Bq"),
        (0x33C4, "cc"),
        (0x33C5, "cd"),
        (0x33C8, "dB"),
        (0x33C9, "Gy"),
        (0x33CA, "ha"),
        (0x33CB, "HP"),
        (0x33CC, "in"),
        (0x33CD, "KK"),
        (0x33CE, "KM"),
        (0x33CF, "kt"),
        (0x33D0, "lm"),
        (0x33D1, "ln"),
        (0x33D2, "log"),
        (0x33D3, "lx"),
        (0x33D4, "mb"),
        (0x33D5, "mil"),
        (0x33D6, "mol"),
        (0x33D7, "PH"),
        (0x33D9, "PPM"),
        (0x33DA, "PR"),
        (0x33DB, "sr"),
        (0x33DC, "Sv"),
        (0x33DD, "Wb"),
        (0x33FF, "gal"),
        (0xFB00, "ff"),
        (0xFB01, "fi"),
        (0xFB02, "fl"),
        (0xFB03, "ffi"),
        (0xFB04, "ffl"),
        (0xFB05, "st"),
        (0xFB06, "st"),
        (0x1F12D, "CD"),
        (0x1F12E, "WZ"),
        (0x1F14A, "HV"),
        (0x1F14B, "MV"),
        (0x1F14C, "SD"),
        (0x1F14D, "SS"),
        (0x1F14E, "PPV"),
        (0x1F14F, "WC"),
        (0x1F16A, "MC"),
        (0x1F16B, "MD"),
        (0x1F16C, "MR"),
        (0x1F190, "DJ"),
    ];

    /// <summary>
    /// Applies the compatibility fold to <paramref name="value"/>.
    /// </summary>
    /// <param name="value">Text that has already been through <see cref="string.Normalize(NormalizationForm)"/>.</param>
    /// <returns>
    /// The folded text, or <paramref name="value"/> itself when nothing in it folds. Characters
    /// outside the table are copied through untouched for the allowlist to judge.
    /// </returns>
    internal static string Apply(string value)
    {
        // No mapped code point is ASCII, so ASCII only text — the overwhelmingly common case —
        // can never fold and is returned without allocating.
        bool ascii = true;
        foreach (char c in value)
        {
            if (!char.IsAscii(c))
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return value;
        }

        StringBuilder? builder = null;

        for (int i = 0; i < value.Length;)
        {
            int codePoint = value[i];
            int width = 1;

            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = char.ConvertToUtf32(value[i], value[i + 1]);
                width = 2;
            }

            if (TryFold(codePoint, out string? replacement))
            {
                builder ??= new StringBuilder(value.Length).Append(value, 0, i);
                builder.Append(replacement);
            }
            else
            {
                builder?.Append(value, i, width);
            }

            i += width;
        }

        return builder?.ToString() ?? value;
    }

    private static bool TryFold(int codePoint, out string? replacement)
    {
        int low = 0;
        int high = Runs.Length - 1;

        while (low <= high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            (int start, int end, char to) = Runs[mid];

            if (codePoint < start)
            {
                high = mid - 1;
            }
            else if (codePoint > end)
            {
                low = mid + 1;
            }
            else
            {
                replacement = ((char)(to + (codePoint - start))).ToString();
                return true;
            }
        }

        low = 0;
        high = Expansions.Length - 1;

        while (low <= high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            (int point, string to) = Expansions[mid];

            if (codePoint < point)
            {
                high = mid - 1;
            }
            else if (codePoint > point)
            {
                low = mid + 1;
            }
            else
            {
                replacement = to;
                return true;
            }
        }

        replacement = null;
        return false;
    }
}
