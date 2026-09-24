using System.Globalization;

namespace Dle.Domain.Attribution;

/// <summary>
/// Short code shown on the interstitial page and typed by the user inside the application (S3,
/// FR-184). On iOS this is the only deterministic deferred strategy that does not require the
/// user to be logged in.
/// </summary>
/// <remarks>
/// A code carries roughly 28 bits of entropy over a 26 character alphabet. That is weak on its
/// own, which is why §E.4.1 (K3) pairs it with a one hour time to live and a rate limit: the code
/// is a single use claim ticket, never a credential.
/// </remarks>
public static class ClaimCode
{
    /// <summary>Number of characters in a claim code.</summary>
    public const int Length = 6;

    /// <summary>
    /// Alphabet used to generate codes. Homoglyphs are excluded on purpose: there is no
    /// <c>B</c>, <c>I</c>, <c>O</c>, <c>S</c>, <c>Z</c>, <c>0</c>, <c>1</c>, <c>2</c>, <c>5</c>
    /// or <c>8</c>, so a code read off a screen cannot be mistyped into a different valid code.
    /// </summary>
    public const string Alphabet = "ACDEFGHJKLMNPQRTUVWXY34679";

    /// <summary>
    /// Normalizes user input before validation or lookup: upper cases it with the invariant
    /// culture and removes whitespace and hyphens, which users add when copying a code from a
    /// screen.
    /// </summary>
    /// <param name="raw">The text the user typed.</param>
    /// <returns>The normalized code. No character is substituted for another — in particular
    /// <c>O</c> is <em>not</em> rewritten to <c>0</c>. Neither character is in
    /// <see cref="Alphabet"/>, and silently rewriting input would risk turning a typo into a
    /// different but valid claim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/> is <see langword="null"/>.</exception>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = raw.Length <= 64 ? stackalloc char[64] : new char[raw.Length];
        int length = 0;

        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                continue;
            }

            buffer[length++] = char.ToUpper(c, CultureInfo.InvariantCulture);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    /// <summary>
    /// Tests whether a code is syntactically a claim code. Callers pass the output of
    /// <see cref="Normalize(string)"/>; this method performs no normalization of its own, so that
    /// the two steps stay separable and separately testable.
    /// </summary>
    /// <param name="code">The already normalized code.</param>
    /// <returns><see langword="true"/> when the code has exactly <see cref="Length"/> characters
    /// and every one of them is in <see cref="Alphabet"/>; otherwise <see langword="false"/>.</returns>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Length)
        {
            return false;
        }

        foreach (char c in code)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
