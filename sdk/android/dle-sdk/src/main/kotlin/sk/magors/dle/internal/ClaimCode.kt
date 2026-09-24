package sk.magors.dle.internal

/**
 * Client side mirror of `Dle.Domain.Attribution.ClaimCode` (S3, FR-184). The two implementations
 * must agree character for character: a code the SDK normalises differently from the server is a
 * code that cannot be redeemed.
 */
internal object ClaimCode {
    /** Number of characters in a claim code. */
    const val LENGTH: Int = 6

    /**
     * Alphabet used to generate codes. Homoglyphs are excluded on purpose: no `B`, `I`, `O`, `S`,
     * `Z`, `0`, `1`, `2`, `5` or `8`, so a code read off a screen cannot be mistyped into a
     * different valid code.
     */
    const val ALPHABET: String = "ACDEFGHJKLMNPQRTUVWXY34679"

    /**
     * Normalises user input before validation: upper cases it and removes whitespace and hyphens,
     * which users add when copying a code from a screen. No character is substituted for
     * another. In particular `O` is not rewritten to `0`: neither is in [ALPHABET], and silently
     * rewriting input would risk turning a typo into a different but valid claim.
     */
    fun normalize(raw: String): String {
        if (raw.isEmpty()) return ""
        val out = StringBuilder(raw.length)
        for (c in raw) {
            if (isDotNetWhiteSpace(c) || c == '-') continue
            out.append(c.uppercaseChar())
        }
        return out.toString()
    }

    /**
     * Tests whether an already normalised code is syntactically a claim code: exactly [LENGTH]
     * characters, every one of them in [ALPHABET]. Performs no normalisation of its own.
     */
    fun isWellFormed(code: String?): Boolean {
        if (code == null || code.length != LENGTH) return false
        for (c in code) if (ALPHABET.indexOf(c) < 0) return false
        return true
    }

    /**
     * `System.Char.IsWhiteSpace` exactly: the Unicode separator categories, the ASCII control
     * whitespace `U+0009`..`U+000D` and `U+0085`. Kotlin's own `isWhitespace` differs at the edges
     * (it includes `U+001C`..`U+001F` and excludes `U+0085`).
     */
    private fun isDotNetWhiteSpace(c: Char): Boolean = when (c.category) {
        CharCategory.SPACE_SEPARATOR, CharCategory.LINE_SEPARATOR, CharCategory.PARAGRAPH_SEPARATOR -> true
        else -> c.code in 0x09..0x0D || c.code == 0x85
    }
}
