package sk.magors.dle

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import sk.magors.dle.internal.InstallReferrerParser
import sk.magors.dle.internal.percentDecode

/** Mirrors `Dle.Domain.Attribution.InstallReferrerParser` (spec §A.2.4, FR-181, TC-141, TC-142). */
class InstallReferrerParserTest {
    @Test
    fun doubleEncodedReferrer_isDecodedOnceThenSplitThenDecodedAgain() {
        val pairs = InstallReferrerParser.parse("dl_cid%3DaB3xK9pQ%26utm_source%3Dfb")

        assertEquals(mapOf("dl_cid" to "aB3xK9pQ", "utm_source" to "fb"), pairs)
        assertEquals("aB3xK9pQ", InstallReferrerParser.clickId("dl_cid%3DaB3xK9pQ%26utm_source%3Dfb"))
    }

    @Test
    fun singleEncodedReferrer_parsesToo() {
        assertEquals(mapOf("dl_cid" to "aB3xK9pQ", "utm_source" to "fb"), InstallReferrerParser.parse("dl_cid=aB3xK9pQ&utm_source=fb"))
    }

    @Test
    fun tripleEncodedValue_isDecodedTwiceOnly() {
        // %2525 -> %25 after the first pass -> % after the second. Two passes, never a loop.
        assertEquals(mapOf("k" to "%41"), InstallReferrerParser.parse("k%3D%252541"))
    }

    @Test
    fun organicInstall_hasNoClickId() {
        val organic = "utm_source=google-play&utm_medium=organic"

        assertEquals(mapOf("utm_source" to "google-play", "utm_medium" to "organic"), InstallReferrerParser.parse(organic))
        assertNull(InstallReferrerParser.clickId(organic))
    }

    @Test
    fun firstOccurrenceWins_soAnAppendedClickIdCannotOverrideTheEngineOne() {
        assertEquals("first", InstallReferrerParser.clickId("dl_cid=first&dl_cid=second"))
    }

    @Test
    fun plusSignIsNotASpace_clickIdsAreOpaqueTokens() {
        assertEquals("a+b", InstallReferrerParser.clickId("dl_cid=a+b"))
    }

    @Test
    fun blankClickId_isOrganic() {
        assertNull(InstallReferrerParser.clickId("dl_cid=%20%20"))
        assertNull(InstallReferrerParser.clickId("dl_cid="))
    }

    @ParameterizedTest
    @ValueSource(strings = ["", "   ", "&&&", "=abc", "novalue", "&=&=", "%"])
    fun malformedInput_yieldsNoPairsAndNoClickId(value: String) {
        assertEquals(emptyMap<String, String>(), InstallReferrerParser.parse(value))
        assertNull(InstallReferrerParser.clickId(value))
    }

    @Test
    fun nullInput_yieldsNothing() {
        assertEquals(emptyMap<String, String>(), InstallReferrerParser.parse(null))
        assertNull(InstallReferrerParser.clickId(null))
        assertNull(InstallReferrerParser.clip(null))
    }

    @Test
    fun malformedEscape_isLeftAsIs() {
        assertEquals("%zz", percentDecode("%zz"))
        assertEquals("%", percentDecode("%"))
        assertEquals("a%2", percentDecode("a%2"))
        assertEquals(mapOf("k" to "%zz"), InstallReferrerParser.parse("k=%zz"))
    }

    @Test
    fun invalidUtf8_becomesReplacementCharacterNotAnException() {
        assertEquals(0xFFFD.toChar().toString(), percentDecode("%FF"))
    }

    @Test
    fun pairCount_isCappedAtMaxPairs() {
        val referrer = (1..InstallReferrerParser.MAX_PAIRS + 8).joinToString("&") { "k$it=v$it" }

        val pairs = InstallReferrerParser.parse(referrer)

        assertEquals(InstallReferrerParser.MAX_PAIRS, pairs.size)
        assertEquals("v1", pairs["k1"])
        assertNull(pairs["k${InstallReferrerParser.MAX_PAIRS + 1}"])
    }

    @Test
    fun oversizedReferrer_isClippedAndTheCutPairIsDropped() {
        val filler = "a".repeat(InstallReferrerParser.MAX_REFERRER_LENGTH - 8)
        val referrer = "dl_cid=x&pad=$filler&tail=value"

        assertTrue(referrer.length > InstallReferrerParser.MAX_REFERRER_LENGTH)
        val pairs = InstallReferrerParser.parse(referrer)

        assertEquals("x", pairs["dl_cid"])
        assertNull(pairs["tail"], "the pair cut in half must not be returned as a prefix of the real value")
        assertEquals(InstallReferrerParser.MAX_REFERRER_LENGTH, InstallReferrerParser.clip(referrer)!!.length)
    }

    @Test
    fun clip_leavesShortValuesAlone() {
        assertEquals("dl_cid=x", InstallReferrerParser.clip("dl_cid=x"))
        assertNull(InstallReferrerParser.clip(""))
    }

    @Test
    fun keyIsSplitOnTheFirstEqualsOnly() {
        assertEquals(mapOf("k" to "a=b"), InstallReferrerParser.parse("k=a=b"))
    }
}
