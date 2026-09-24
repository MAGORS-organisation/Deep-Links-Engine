package sk.magors.dle

import kotlinx.serialization.builtins.ListSerializer
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import sk.magors.dle.internal.ConsentWire
import sk.magors.dle.internal.DleJson
import sk.magors.dle.internal.EventBatch
import sk.magors.dle.internal.EventWire
import sk.magors.dle.internal.Iso8601
import sk.magors.dle.internal.ResolveRequest
import sk.magors.dle.internal.ResolveResponse
import sk.magors.dle.internal.SignalsWire
import sk.magors.dle.internal.toDeferredLink
import sk.magors.dle.internal.toWire

/**
 * The SDK wire contract of spec §B.7.2, asserted against the literal bodies of
 * `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`. A body produced here must be byte
 * identical to what the server's own serializer produces for the same DTO, and every response
 * literal the server emits must decode.
 */
class WireModelsTest {
    @Test
    fun resolveRequest_serialisesToTheSnakeCaseShapeOfB72() {
        val request = ResolveRequest(
            installId = CONTRACT_INSTALL_ID,
            platform = "android",
            appVersion = "3.4.1",
            osVersion = "15",
            referrer = "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
            claimCode = null,
            signals = SignalsWire(language = "sk-SK", screen = "1080x2400", tzOffset = 120),
            consent = ConsentWire(analytics = true, attribution = true, ts = Iso8601.format(CONTRACT_MOMENT_MILLIS)),
        )

        assertEquals(RESOLVE_REQUEST_LITERAL, DleJson.encodeToString(ResolveRequest.serializer(), request))
    }

    @Test
    fun resolveRequest_omitsAbsentOptionalMembersInsteadOfWritingNull() {
        val request = ResolveRequest(installId = "a", platform = "android")

        assertEquals("""{"install_id":"a","platform":"android"}""", DleJson.encodeToString(ResolveRequest.serializer(), request))
    }

    @Test
    fun resolveResponse_decodesTheLiteralOfB72() {
        val link = DleJson.decodeFromString(ResolveResponse.serializer(), RESOLVE_RESPONSE_LITERAL).toDeferredLink()

        assertTrue(link.matched)
        assertEquals(MatchType.INSTALL_REFERRER, link.matchType)
        assertEquals(1.0, link.confidence)
        assertEquals("aB3xK9pQ", link.clickId)
        assertEquals(ResolvedLink(id = "7286414500000000001", deeplinkPath = "/promo/jesen", campaign = "jesen26"), link.link)
        assertEquals(mapOf("utm_source" to "fb", "utm_campaign" to "jesen26", "promo" to "AUTUMN20"), link.params)
        assertEquals(0, link.expiresIn)
        assertTrue(link.isDeterministic)
        assertTrue(link.isFinal)
    }

    @Test
    fun resolveResponse_unmatchedInstallHasNoClickIdAndNoLink() {
        val body = """{"matched":false,"match_type":"none","confidence":0,"params":{},"expires_in":0}"""

        val link = DleJson.decodeFromString(ResolveResponse.serializer(), body).toDeferredLink()

        assertEquals(DeferredLink.none(), link)
        assertNull(link.clickId)
        assertNull(link.link)
    }

    @Test
    fun resolveResponse_paramsKeepTheirOriginalSpelling() {
        val body = """{"matched":true,"match_type":"direct_open","confidence":1.0,"params":{"utmSource":"fb","PromoCode":"AUTUMN20"},"expires_in":0}"""

        val link = DleJson.decodeFromString(ResolveResponse.serializer(), body).toDeferredLink()

        assertEquals(mapOf("utmSource" to "fb", "PromoCode" to "AUTUMN20"), link.params)
    }

    @Test
    fun resolveResponse_unknownMembersAreIgnored() {
        val body = """{"matched":true,"match_type":"login","confidence":1.0,"future_member":{"x":1},"link":{"id":"1","colour":"red"}}"""

        val link = DleJson.decodeFromString(ResolveResponse.serializer(), body).toDeferredLink()

        assertEquals(MatchType.LOGIN, link.matchType)
        assertEquals("1", link.link?.id)
        assertEquals(emptyMap<String, String>(), link.params)
    }

    @Test
    fun resolveResponse_unknownMatchTypeIsRefusedNotGuessed() {
        val body = """{"matched":true,"match_type":"telepathy","confidence":1.0}"""

        assertThrows<DleException.Malformed> {
            DleJson.decodeFromString(ResolveResponse.serializer(), body).toDeferredLink()
        }
    }

    @Test
    fun resolveResponse_missingMatchTypeIsRefused() {
        val body = """{"matched":true,"confidence":1.0}"""

        assertThrows<kotlinx.serialization.SerializationException> {
            DleJson.decodeFromString(ResolveResponse.serializer(), body)
        }
    }

    @Test
    fun resolveResponse_probabilisticMatchIsNeverDeterministic() {
        val body = """{"matched":true,"match_type":"probabilistic","confidence":1.0,"click_id":"x"}"""

        val link = DleJson.decodeFromString(ResolveResponse.serializer(), body).toDeferredLink()

        assertFalse(link.isDeterministic)
    }

    @Test
    fun deferredLink_roundTripsThroughTheWireForm() {
        val original = DleJson.decodeFromString(ResolveResponse.serializer(), RESOLVE_RESPONSE_LITERAL).toDeferredLink()

        val text = DleJson.encodeToString(ResolveResponse.serializer(), original.toWire())
        val restored = DleJson.decodeFromString(ResolveResponse.serializer(), text).toDeferredLink()

        assertEquals(original, restored)
    }

    @Test
    fun eventBatch_serialisesToTheSnakeCaseShapeOfB72() {
        val events = listOf(
            DleEvent.linkOpen("https://link.zak.sk/aB3xK9pQ", CONTRACT_MOMENT_MILLIS),
            DleEvent(
                type = DleEventType.CONVERSION,
                name = "purchase",
                value = 24.9,
                currency = "EUR",
                timestampMillis = CONTRACT_MOMENT_MILLIS + 5 * 60_000L,
            ),
        )
        val batch = EventBatch(installId = CONTRACT_INSTALL_ID, events = events.map { it.toWire(nowMillis = 0L) })

        assertEquals(EVENT_BATCH_LITERAL, DleJson.encodeToString(EventBatch.serializer(), batch))
    }

    @Test
    fun event_withoutTimestampIsStampedWithNow() {
        val wire = DleEvent.firstOpen().toWire(nowMillis = CONTRACT_MOMENT_MILLIS)

        assertEquals("2026-09-03T10:00:00+00:00", wire.ts)
        assertEquals("""{"type":"first_open","ts":"2026-09-03T10:00:00+00:00"}""", DleJson.encodeToString(EventWire.serializer(), wire))
    }

    @Test
    fun eventList_roundTripsForTheOfflineBuffer() {
        val events = listOf(DleEvent.custom("signup", mapOf("plan" to "pro")).toWire(CONTRACT_MOMENT_MILLIS))
        val serializer = ListSerializer(EventWire.serializer())

        val restored = DleJson.decodeFromString(serializer, DleJson.encodeToString(serializer, events))

        assertEquals(events, restored)
    }

    @Test
    fun consent_withoutARecordedDecisionIsNotSent() {
        assertNull(DleConsent.denied().toWire())
        assertNull(DleConsent.granted().toWire())
    }

    @Test
    fun consent_withARecordedDecisionCarriesItsTimestamp() {
        val wire = DleConsent.analyticsOnly(CONTRACT_MOMENT_MILLIS).toWire()

        assertEquals(ConsentWire(analytics = true, attribution = false, ts = "2026-09-03T10:00:00+00:00"), wire)
    }

    companion object {
        const val RESOLVE_REQUEST_LITERAL: String =
            """{"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","platform":"android","app_version":"3.4.1","os_version":"15","referrer":"dl_cid%3DaB3xK9pQ%26utm_source%3Dfb","signals":{"language":"sk-SK","screen":"1080x2400","tz_offset":120},"consent":{"analytics":true,"attribution":true,"ts":"2026-09-03T10:00:00+00:00"}}"""

        const val RESOLVE_RESPONSE_LITERAL: String =
            """{"matched":true,"match_type":"install_referrer","confidence":1.0,"click_id":"aB3xK9pQ","link":{"id":"7286414500000000001","deeplink_path":"/promo/jesen","campaign":"jesen26"},"params":{"utm_source":"fb","utm_campaign":"jesen26","promo":"AUTUMN20"},"expires_in":0}"""

        const val EVENT_BATCH_LITERAL: String =
            """{"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","events":[{"type":"link_open","url":"https://link.zak.sk/aB3xK9pQ","ts":"2026-09-03T10:00:00+00:00"},{"type":"conversion","name":"purchase","value":24.9,"currency":"EUR","ts":"2026-09-03T10:05:00+00:00"}]}"""
    }
}
