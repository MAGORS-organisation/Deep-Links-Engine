package net.kyberware.dle

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ProblemDocumentTest {
    @Test
    fun code_isTheLastPathSegment_whateverTheHost() {
        assertEquals("rate-limited", ProblemDocument.codeOf("https://docs.example/problems/rate-limited"))
        assertEquals("rate-limited", ProblemDocument.codeOf("https://other.example/errors/rate-limited/?v=2#top"))
        assertEquals("rate-limited", ProblemDocument.codeOf("rate-limited"))
    }

    @Test
    fun code_isNull_withoutAType() {
        assertNull(ProblemDocument.codeOf(null))
        assertNull(ProblemDocument.codeOf(""))
        assertNull(ProblemDocument(type = null).code)
    }

    @Test
    fun claimCodeFailure_isRecognisedByItsCode_onAnyHost() {
        val failure = DleException.Http(
            status = 400,
            problem = ProblemDocument(type = "https://problems.example/problems/claim-code-invalid"),
        )

        assertTrue(failure.isClaimCodeInvalid)
    }
}
