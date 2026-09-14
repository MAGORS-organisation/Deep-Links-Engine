package sk.magors.dle

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import sk.magors.dle.internal.ClaimCode

/**
 * Mirrors `Dle.Domain.Attribution.ClaimCode` (S3, FR-184). The two implementations must agree
 * character for character: a code the SDK normalises differently from the server cannot be
 * redeemed.
 */
class ClaimCodeTest {
    @Test
    fun constants_matchTheServer() {
        assertEquals(6, ClaimCode.LENGTH)
        assertEquals("ACDEFGHJKLMNPQRTUVWXY34679", ClaimCode.ALPHABET)
    }

    @Test
    fun alphabet_excludesEveryHomoglyph() {
        for (c in "BIOSZ01258") assertFalse(ClaimCode.ALPHABET.contains(c), "'$c' must not be in the alphabet")
    }

    @Test
    fun normalize_upperCasesAndStripsWhitespaceAndHyphens() {
        assertEquals("ACDEFG", ClaimCode.normalize(" ac-d ef\tg "))
        assertEquals("ACDEFG", ClaimCode.normalize("ACD-EFG"))
        assertEquals("ACDEFG", ClaimCode.normalize("acd efg"))
        assertEquals("ACDEFG", ClaimCode.normalize("a\nc\rdef g"))
    }

    @Test
    fun normalize_emptyInputStaysEmpty() {
        assertEquals("", ClaimCode.normalize(""))
        assertEquals("", ClaimCode.normalize(" - - "))
    }

    @Test
    fun normalize_neverSubstitutesOneCharacterForAnother() {
        // `O` is not rewritten to `0` and `I` is not rewritten to `1`: neither is in the alphabet
        // and a silent rewrite could turn a typo into a different but valid claim.
        assertEquals("0O1I", ClaimCode.normalize("0o1i"))
        assertFalse(ClaimCode.isWellFormed(ClaimCode.normalize("ACDEF0")))
        assertFalse(ClaimCode.isWellFormed(ClaimCode.normalize("ACDEFO")))
    }

    @Test
    fun normalize_usesDotNetWhitespaceSemantics() {
        // Code points are spelled numerically so no editor can silently mangle an invisible
        // character in this file.
        fun withSeparator(codePoint: Int): String = "ACD" + codePoint.toChar() + "EFG"

        // NEL (U+0085), NBSP (U+00A0), ideographic space (U+3000) and the line and paragraph
        // separators (U+2028, U+2029) are whitespace to System.Char.IsWhiteSpace and are removed,
        // as are the ASCII controls U+0009..U+000D.
        for (codePoint in listOf(0x0085, 0x00A0, 0x3000, 0x2028, 0x2029, 0x0009, 0x000B, 0x000C)) {
            assertEquals("ACDEFG", ClaimCode.normalize(withSeparator(codePoint)), "U+%04X".format(codePoint))
        }
        // The information separators U+001C..U+001F are not, even though Kotlin's own
        // isWhitespace says they are. They stay, and the code is then not well formed.
        for (codePoint in listOf(0x001C, 0x001D, 0x001E, 0x001F)) {
            val normalized = ClaimCode.normalize(withSeparator(codePoint))
            assertEquals(7, normalized.length, "U+%04X must be kept".format(codePoint))
            assertFalse(ClaimCode.isWellFormed(normalized))
        }
    }

    @ParameterizedTest
    @ValueSource(strings = ["ACDEFG", "HJKLMN", "PQRTUV", "WXY346", "79ACDE", "YYYYYY"])
    fun isWellFormed_acceptsSixCharactersFromTheAlphabet(code: String) {
        assertTrue(ClaimCode.isWellFormed(code))
    }

    @ParameterizedTest
    @ValueSource(strings = ["", "ACDEF", "ACDEFGH", "acdefg", "ACDEFB", "ACDEF0", "ACD-EF", "ACD EF", "ACDEF1", "ACDEF8"])
    fun isWellFormed_rejectsWrongLengthLowerCaseAndForeignCharacters(code: String) {
        assertFalse(ClaimCode.isWellFormed(code))
    }

    @Test
    fun isWellFormed_rejectsNonAsciiLookalikes() {
        // A Latin A with an acute accent (U+00C1) is not the A of the alphabet.
        assertFalse(ClaimCode.isWellFormed(0x00C1.toChar() + "CDEFG"))
    }

    @Test
    fun isWellFormed_rejectsNull() {
        assertFalse(ClaimCode.isWellFormed(null))
    }

    @Test
    fun isWellFormed_performsNoNormalisationOfItsOwn() {
        assertFalse(ClaimCode.isWellFormed(" ACDEFG"))
        assertTrue(ClaimCode.isWellFormed(ClaimCode.normalize(" ACDEFG")))
    }
}
