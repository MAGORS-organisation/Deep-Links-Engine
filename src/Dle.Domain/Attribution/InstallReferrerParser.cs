using System.Collections.ObjectModel;

namespace Dle.Domain.Attribution;

/// <summary>
/// Parses the Android Play Install Referrer string (§A.2.4). This is the one deterministic
/// channel that survives an installation, so everything about this parser is written to be total:
/// it never throws and it never guesses.
/// </summary>
/// <remarks>
/// <para>
/// The raw value is a query string that the Play Store returns URL encoded, and it is frequently
/// encoded a second time on the way in, so a value such as
/// <c>dl_cid%3DaB3xK9pQ%26utm_source%3Dfb</c> is normal. The parser therefore percent decodes the
/// whole string once, splits it on <c>&amp;</c>, splits each pair on the <em>first</em> <c>=</c>,
/// and percent decodes the key and the value once more.
/// </para>
/// <para>
/// A plus sign is left alone rather than being turned into a space: click identifiers are opaque
/// tokens and rewriting them would break the exact match this strategy exists for.
/// </para>
/// <para>
/// The input is attacker influenced, so the work is bounded twice: at most
/// <see cref="MaxReferrerLength"/> characters are examined and at most <see cref="MaxPairs"/>
/// pairs are returned. Google does not publish a maximum length for the parameter, which is
/// precisely why one is imposed here.
/// </para>
/// </remarks>
public static class InstallReferrerParser
{
    /// <summary>Key under which the engine carries the click identifier into the store URL.</summary>
    public const string ClickIdKey = "dl_cid";

    /// <summary>Maximum number of characters of the raw referrer that are examined.</summary>
    public const int MaxReferrerLength = 1024;

    /// <summary>Maximum number of key and value pairs returned by <see cref="Parse(string?)"/>.</summary>
    public const int MaxPairs = 32;

    /// <summary>
    /// Parses the referrer into its key and value pairs.
    /// </summary>
    /// <param name="referrer">The raw referrer string as returned by the Play Install Referrer
    /// API, or <see langword="null"/>.</param>
    /// <returns>The parsed pairs, with ordinal, case sensitive keys. When a key occurs more than
    /// once the <em>first</em> occurrence wins, so that an attacker appending a second
    /// <c>dl_cid</c> cannot override the one the engine wrote. Malformed input yields an empty
    /// dictionary; this method never throws.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer))
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        bool truncated = referrer.Length > MaxReferrerLength;
        string bounded = truncated ? referrer[..MaxReferrerLength] : referrer;
        string decoded = Unescape(bounded);

        string[] segments = decoded.Split('&');

        // A truncated input may have cut the final pair in half; dropping it is safer than
        // returning a value that is a prefix of the real one.
        int segmentCount = truncated && segments.Length > 1 ? segments.Length - 1 : segments.Length;

        Dictionary<string, string>? pairs = null;

        for (int i = 0; i < segmentCount; i++)
        {
            if (pairs is not null && pairs.Count >= MaxPairs)
            {
                break;
            }

            string segment = segments[i];
            int separator = segment.IndexOf('=');

            // No separator at all, or an empty key: not a pair, so it is dropped.
            if (separator <= 0)
            {
                continue;
            }

            string key = Unescape(segment[..separator]);
            if (key.Length == 0)
            {
                continue;
            }

            string value = Unescape(segment[(separator + 1)..]);

            pairs ??= new Dictionary<string, string>(StringComparer.Ordinal);
            pairs.TryAdd(key, value);
        }

        return pairs is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(pairs);
    }

    /// <summary>
    /// Extracts the click identifier written by the engine into the store URL (FR-181).
    /// </summary>
    /// <param name="referrer">The raw referrer string, or <see langword="null"/>.</param>
    /// <param name="clickId">The click identifier when one was present, otherwise
    /// <see cref="string.Empty"/>.</param>
    /// <returns><see langword="true"/> when a non empty <c>dl_cid</c> was found. An organic
    /// install has no <c>dl_cid</c> and legitimately returns <see langword="false"/> (TC-142).</returns>
    public static bool TryGetClickId(string? referrer, out string clickId)
    {
        IReadOnlyDictionary<string, string> values = Parse(referrer);

        if (values.TryGetValue(ClickIdKey, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            clickId = value.Trim();
            return true;
        }

        clickId = string.Empty;
        return false;
    }

    private static string Unescape(string value)
    {
        if (value.Length == 0 || !value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            // Explicit default: a malformed escape sequence leaves the text as it was. The
            // contract of this type is that it never throws, and a referrer we cannot decode is
            // simply a referrer that will not match anything.
            return value;
        }
    }
}
