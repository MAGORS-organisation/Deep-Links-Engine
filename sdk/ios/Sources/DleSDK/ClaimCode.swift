import Foundation

/// The six-character claim code of strategy S3 (FR-184): the user reads it off the interstitial
/// page and types it into the app. This is the only deterministic deferred strategy on iOS that
/// does not require the user to be signed in (spec §A.2.4, §B.6.3).
///
/// This type mirrors `Dle.Domain.Attribution.ClaimCode` **exactly** — same alphabet, same
/// characters stripped, same case folding, same length — so that a code the SDK accepts locally
/// is a code the engine accepts, and vice versa. The C# it mirrors:
///
/// ```csharp
/// public const int Length = 6;
/// public const string Alphabet = "ACDEFGHJKLMNPQRTUVWXY34679";
///
/// public static string Normalize(string raw)
/// {
///     ArgumentNullException.ThrowIfNull(raw);
///     if (raw.Length == 0) return string.Empty;
///     Span<char> buffer = raw.Length <= 64 ? stackalloc char[64] : new char[raw.Length];
///     int length = 0;
///     foreach (char c in raw)
///     {
///         if (char.IsWhiteSpace(c) || c == '-') continue;
///         buffer[length++] = char.ToUpper(c, CultureInfo.InvariantCulture);
///     }
///     return length == 0 ? string.Empty : new string(buffer[..length]);
/// }
///
/// public static bool IsWellFormed(string? code)
/// {
///     if (code is null || code.Length != Length) return false;
///     foreach (char c in code)
///         if (!Alphabet.Contains(c, StringComparison.Ordinal)) return false;
///     return true;
/// }
/// ```
///
/// A code is a single-use claim ticket with a one-hour time to live and a rate limit behind it
/// (spec §E.4.1, K3) — never a credential. Never put one on a custom-scheme URL.
public enum DleClaimCode {
    /// Number of characters in a claim code (`ClaimCode.Length`).
    public static let length = 6

    /// Alphabet codes are generated from (`ClaimCode.Alphabet`). Homoglyphs are excluded on
    /// purpose: there is no `B`, `I`, `O`, `S`, `Z`, `0`, `1`, `2`, `5` or `8`, so a code read
    /// off a screen cannot be mistyped into a different valid code.
    public static let alphabet = "ACDEFGHJKLMNPQRTUVWXY34679"

    /// The alphabet as scalars, for membership tests.
    private static let alphabetScalars: Set<Unicode.Scalar> = Set(alphabet.unicodeScalars)

    /// Normalises user input before validation or lookup (`ClaimCode.Normalize`): removes
    /// whitespace and hyphens — which users add when copying a code from a screen — and upper
    /// cases the rest with the invariant (locale-independent) mapping.
    ///
    /// No character is substituted for another; in particular `O` is **not** rewritten to `0`.
    /// Neither is in ``alphabet``, and silently rewriting input would risk turning a typo into a
    /// different but valid claim.
    ///
    /// Equivalence with the C#: `char.IsWhiteSpace` is the Unicode White_Space property, which is
    /// what `Character.isWhitespace` tests; `char.ToUpper(c, InvariantCulture)` is the simple
    /// (one-to-one) upper-case mapping, so a character whose upper-case form is longer than one
    /// character (`ß`) is kept as it is, exactly as the C# keeps it. The C# iterates UTF-16
    /// units and this iterates characters; the results are identical for every input, because
    /// only ASCII can ever survive ``isWellFormed(_:)`` and a multi-unit character is neither
    /// whitespace nor a hyphen on either side.
    ///
    /// - Parameter raw: The text the user typed.
    /// - Returns: The normalised code, possibly empty. Idempotent.
    public static func normalize(_ raw: String) -> String {
        guard !raw.isEmpty else { return "" }
        var out = ""
        out.reserveCapacity(raw.utf16.count)
        for character in raw {
            if character.isWhitespace || character == "-" {
                continue
            }
            let upper = String(character).uppercased()
            if upper.count == 1 {
                out.append(upper)
            } else {
                out.append(character)
            }
        }
        return out
    }

    /// Tests whether a code is syntactically a claim code (`ClaimCode.IsWellFormed`): exactly
    /// ``length`` characters, every one of them in ``alphabet``.
    ///
    /// Callers pass the output of ``normalize(_:)``; this performs no normalisation of its own
    /// (`"acdefg"` is **not** well formed), so that the two steps stay separable and separately
    /// testable. The length is counted in UTF-16 units, as `string.Length` counts it.
    ///
    /// - Parameter code: The already normalised code, or `nil`.
    public static func isWellFormed(_ code: String?) -> Bool {
        guard let code, code.utf16.count == length else { return false }
        for scalar in code.unicodeScalars where !alphabetScalars.contains(scalar) {
            return false
        }
        return true
    }
}
