package sk.magors.dle

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import sk.magors.dle.internal.CachedResolve
import sk.magors.dle.internal.ReferrerOutcome
import sk.magors.dle.internal.ReferrerStatus
import sk.magors.dle.internal.StateStore

/** What the SDK remembers between launches, and the resolve-once rule built on it (TC-143). */
class StateStoreTest {
    private val prefs = FakeSharedPreferences()
    private val store = StateStore(prefs)

    private val attributed = DeferredLink(
        matched = true,
        matchType = MatchType.INSTALL_REFERRER,
        confidence = 1.0,
        clickId = "aB3xK9pQ",
        link = ResolvedLink(id = "7286414500000000001", deeplinkPath = "/promo/jesen", campaign = "jesen26"),
        params = mapOf("utm_source" to "fb", "promo" to "AUTUMN20"),
    )

    @Test
    fun startsEmpty() {
        assertFalse(store.isResolveDone)
        assertNull(store.cachedResolve())
        assertFalse(store.isReferrerAttempted)
        assertNull(store.referrerOutcome())
        assertFalse(store.isFirstOpenTracked)
        assertNull(store.consent())
    }

    @Test
    fun resolve_isRememberedWithItsTime() {
        store.saveResolve(attributed, atMillis = CONTRACT_MOMENT_MILLIS)

        assertTrue(store.isResolveDone)
        val cached = store.cachedResolve()!!
        assertEquals(attributed, cached.link)
        assertEquals(CONTRACT_MOMENT_MILLIS, cached.resolvedAtMillis)
    }

    @Test
    fun resolve_survivesAReopenOfTheSamePreferences() {
        store.saveResolve(DeferredLink.none(), atMillis = CONTRACT_MOMENT_MILLIS)

        val reopened = StateStore(prefs)

        assertTrue(reopened.isResolveDone)
        assertEquals(DeferredLink.none(), reopened.cachedResolve()!!.link)
    }

    @Test
    fun resolveOnce_aFinalAnswerIsNeverRequestedAgain() {
        val cached = CachedResolve(DeferredLink.none(), resolvedAtMillis = CONTRACT_MOMENT_MILLIS)

        assertFalse(cached.needsRequest(ResolveOptions.NONE, CONTRACT_MOMENT_MILLIS))
        assertFalse(cached.needsRequest(ResolveOptions.NONE, CONTRACT_MOMENT_MILLIS + 365L * 24 * 3_600_000L))
    }

    @Test
    fun resolveOnce_newEvidenceIsANewQuestionUnlessTheAnswerIsAlreadyDeterministic() {
        val organic = CachedResolve(DeferredLink.none(), resolvedAtMillis = CONTRACT_MOMENT_MILLIS)
        val deterministic = CachedResolve(attributed, resolvedAtMillis = CONTRACT_MOMENT_MILLIS)

        assertTrue(organic.needsRequest(ResolveOptions.claimCode("ACDEFG"), CONTRACT_MOMENT_MILLIS))
        assertTrue(organic.needsRequest(ResolveOptions.loginKey("hashed"), CONTRACT_MOMENT_MILLIS))
        assertFalse(deterministic.needsRequest(ResolveOptions.claimCode("ACDEFG"), CONTRACT_MOMENT_MILLIS))
        assertFalse(organic.needsRequest(ResolveOptions(claimCode = "  "), CONTRACT_MOMENT_MILLIS), "blank evidence is no evidence")
    }

    @Test
    fun resolveOnce_aNonFinalAnswerIsRequestedAgainOnlyAfterItExpired() {
        val hint = DeferredLink(matched = true, matchType = MatchType.PROBABILISTIC, confidence = 0.6, expiresIn = 600)
        val cached = CachedResolve(hint, resolvedAtMillis = CONTRACT_MOMENT_MILLIS)

        assertFalse(cached.needsRequest(ResolveOptions.NONE, CONTRACT_MOMENT_MILLIS + 599_999L))
        assertTrue(cached.needsRequest(ResolveOptions.NONE, CONTRACT_MOMENT_MILLIS + 600_000L))
    }

    @Test
    fun corruptCache_readsAsNoResolveRatherThanCrashing() {
        prefs.edit().putBoolean(StateStore.KEY_RESOLVE_DONE, true).putString(StateStore.KEY_RESOLVE_LINK, "{oops").commit()

        assertNull(store.cachedResolve())
    }

    @Test
    fun cacheWithAnUnknownMatchType_isDiscarded() {
        prefs.edit()
            .putBoolean(StateStore.KEY_RESOLVE_DONE, true)
            .putString(StateStore.KEY_RESOLVE_LINK, """{"matched":true,"match_type":"telepathy","confidence":1.0}""")
            .commit()

        assertNull(store.cachedResolve())
    }

    @Test
    fun referrerOutcome_isRememberedOnce() {
        store.saveReferrerOutcome(ReferrerOutcome(ReferrerStatus.OK, "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb"))

        assertTrue(store.isReferrerAttempted)
        val outcome = store.referrerOutcome()!!
        assertEquals(ReferrerStatus.OK, outcome.status)
        assertEquals("aB3xK9pQ", outcome.clickId)
        assertTrue(outcome.status.isTerminal)
    }

    @Test
    fun referrerOutcome_withoutAReferrerIsStillAnAttempt() {
        store.saveReferrerOutcome(ReferrerOutcome(ReferrerStatus.TIMEOUT))

        assertTrue(store.isReferrerAttempted)
        assertNull(store.referrerOutcome()!!.referrer)
        assertNull(store.referrerOutcome()!!.clickId)
        assertFalse(ReferrerStatus.TIMEOUT.isTerminal)
        assertTrue(ReferrerStatus.NOT_SUPPORTED.isTerminal)
        assertTrue(ReferrerStatus.PERMISSION_ERROR.isTerminal)
        assertTrue(ReferrerStatus.DEVELOPER_ERROR.isTerminal)
    }

    @Test
    fun firstOpenFlag_persists() {
        store.isFirstOpenTracked = true

        assertTrue(StateStore(prefs).isFirstOpenTracked)
    }

    @Test
    fun consent_isRememberedWithItsTimestamp() {
        store.saveConsent(DleConsent.analyticsOnly(CONTRACT_MOMENT_MILLIS))

        assertEquals(DleConsent(analytics = true, attribution = false, timestampMillis = CONTRACT_MOMENT_MILLIS), store.consent())
    }

    @Test
    fun consent_withoutATimestampIsNotRecorded() {
        store.saveConsent(DleConsent.granted())

        assertNull(store.consent())
    }

    @Test
    fun clear_forgetsEverything() {
        store.saveResolve(attributed, CONTRACT_MOMENT_MILLIS)
        store.saveReferrerOutcome(ReferrerOutcome(ReferrerStatus.OK, "dl_cid=x"))
        store.isFirstOpenTracked = true
        store.saveConsent(DleConsent.granted(CONTRACT_MOMENT_MILLIS))

        store.clear()

        assertFalse(store.isResolveDone)
        assertNull(store.referrerOutcome())
        assertFalse(store.isFirstOpenTracked)
        assertNull(store.consent())
    }
}
