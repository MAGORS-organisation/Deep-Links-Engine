package sk.magors.dle

import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import sk.magors.dle.internal.DleApi
import sk.magors.dle.internal.DleJson
import sk.magors.dle.internal.EventBatch
import sk.magors.dle.internal.ResolveRequest
import sk.magors.dle.internal.SignalsWire
import sk.magors.dle.internal.TraceContext
import sk.magors.dle.internal.toWire

/** The HTTP client against a `MockWebServer` standing in for `dle-control`. */
class DleApiTest {
    private lateinit var server: MockWebServer
    private var signalsRequested = 0

    @BeforeEach
    fun start() {
        server = MockWebServer()
        server.start()
        signalsRequested = 0
    }

    @AfterEach
    fun stop() {
        server.shutdown()
    }

    private fun config(probabilisticSignals: Boolean = false, configure: DleConfig.Builder.() -> Unit = {}): DleConfig =
        DleConfig.builder("http://127.0.0.1:${server.port}", "dle_pk_test")
            .probabilisticSignals(probabilisticSignals)
            .logLevel(DleLogLevel.OFF)
            .apply(configure)
            .build()

    private fun api(config: DleConfig = config()): DleApi = DleApi(
        config = config,
        signalsProvider = {
            signalsRequested++
            SignalsWire(language = "sk-SK", screen = "1080x2400", tzOffset = 120)
        },
        log = silentLog(),
    )

    private fun contractRequest(api: DleApi, consent: DleConsent = DleConsent.granted(CONTRACT_MOMENT_MILLIS)): ResolveRequest =
        api.resolveRequest(
            installId = CONTRACT_INSTALL_ID,
            appVersion = "3.4.1",
            osVersion = "15",
            referrer = "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
            options = ResolveOptions.NONE,
            consent = consent,
        )

    @Test
    fun resolve_postsTheContractBodyWithTheRequiredHeaders() {
        server.enqueue(json(200, WireModelsTest.RESOLVE_RESPONSE_LITERAL))
        val api = api(config(probabilisticSignals = true))

        val link = runBlocking { api.resolve(contractRequest(api)) }

        val recorded = server.takeRequest()
        assertEquals("POST", recorded.method)
        assertEquals("/v1/resolve", recorded.path)
        assertEquals("Bearer dle_pk_test", recorded.getHeader("Authorization"))
        assertTrue(recorded.getHeader("Content-Type").orEmpty().startsWith("application/json"), "Content-Type: ${recorded.getHeader("Content-Type")}")
        assertTrue(TraceContext.PATTERN.matches(recorded.getHeader("traceparent").orEmpty()), "traceparent must be W3C shaped")
        assertEquals("dle-android/" + Dle.VERSION, recorded.getHeader("User-Agent"))
        assertEquals(WireModelsTest.RESOLVE_REQUEST_LITERAL, recorded.body.readUtf8())
        assertEquals(MatchType.INSTALL_REFERRER, link.matchType)
        assertEquals("aB3xK9pQ", link.clickId)
        assertTrue(link.isDeterministic)
    }

    @Test
    fun resolve_sendsAFreshTraceparentPerRequest() {
        server.enqueue(json(200, WireModelsTest.RESOLVE_RESPONSE_LITERAL))
        server.enqueue(json(200, WireModelsTest.RESOLVE_RESPONSE_LITERAL))
        val api = api()

        runBlocking {
            api.resolve(contractRequest(api))
            api.resolve(contractRequest(api))
        }

        val first = server.takeRequest().getHeader("traceparent")
        val second = server.takeRequest().getHeader("traceparent")
        assertNotNull(first)
        assertTrue(first != second)
    }

    @Test
    fun signals_areOmittedEntirelyWithoutAttributionConsent() {
        val api = api(config(probabilisticSignals = true))
        val consent = DleConsent(analytics = true, attribution = false, timestampMillis = CONTRACT_MOMENT_MILLIS)

        val request = contractRequest(api, consent)

        assertNull(request.signals)
        assertEquals(0, signalsRequested, "signals must not even be collected without consent")
        val body = DleJson.encodeToString(ResolveRequest.serializer(), request)
        assertFalse(body.contains("signals"), "the member is absent, not empty: $body")
        assertTrue(body.contains(""""consent":{"analytics":true,"attribution":false,"ts":"2026-09-03T10:00:00+00:00"}"""))
    }

    @Test
    fun signals_areOmittedWhenTheOperatorDidNotOptInEvenWithConsent() {
        val api = api(config(probabilisticSignals = false))

        val request = contractRequest(api, DleConsent.granted(CONTRACT_MOMENT_MILLIS))

        assertNull(request.signals)
        assertEquals(0, signalsRequested)
    }

    @Test
    fun signals_areOmittedForTheUnrecordedDefaultConsent() {
        val api = api(config(probabilisticSignals = true))

        val request = contractRequest(api, DleConsent.denied())

        assertNull(request.signals)
        assertNull(request.consent, "an unrecorded decision is not sent as an explicit refusal")
    }

    @Test
    fun signals_areAttachedOnlyWhenBothGatesAreOpen() {
        val api = api(config(probabilisticSignals = true))

        val request = contractRequest(api, DleConsent.granted(CONTRACT_MOMENT_MILLIS))

        assertEquals(SignalsWire(language = "sk-SK", screen = "1080x2400", tzOffset = 120), request.signals)
        assertEquals(1, signalsRequested)
    }

    @Test
    fun resolveRequest_carriesTheEvidenceAndClipsTheReferrer() {
        val api = api()
        val longReferrer = "dl_cid=x&pad=" + "a".repeat(2000)

        val request = api.resolveRequest(
            installId = CONTRACT_INSTALL_ID,
            appVersion = null,
            osVersion = null,
            referrer = longReferrer,
            options = ResolveOptions(claimCode = "ACDEFG", loginKey = "hashed"),
            consent = DleConsent.denied(),
        )

        assertEquals("ACDEFG", request.claimCode)
        assertEquals("hashed", request.loginKey)
        assertEquals(1024, request.referrer!!.length)
        assertEquals("android", request.platform)
    }

    @Test
    fun rateLimit_isReportedWithTheProblemAndRetryAfter() {
        server.enqueue(
            MockResponse()
                .setResponseCode(429)
                .setHeader("Content-Type", "application/problem+json")
                .setHeader("Retry-After", "30")
                .setBody(
                    """{"type":"https://docs.dle.dev/problems/rate-limited","title":"Too many requests","status":429,"detail":"Slow down.","instance":"/v1/resolve"}""",
                ),
        )
        val api = api()

        val failure = assertThrows<DleException.Http> { runBlocking { api.resolve(contractRequest(api)) } }

        assertEquals(429, failure.status)
        assertTrue(failure.isRateLimited)
        assertTrue(failure.isRetriable)
        assertEquals(30_000L, failure.retryAfterMillis)
        assertEquals(ProblemDocument.TYPE_RATE_LIMITED, failure.problem?.type)
        assertEquals("Too many requests", failure.problem?.title)
        assertEquals(429, failure.problem?.status)
        assertEquals("Slow down.", failure.problem?.detail)
        assertEquals("/v1/resolve", failure.problem?.instance)
        assertEquals("Too many requests", failure.message)
    }

    @Test
    fun claimCodeFailure_exposesReasonAndCanReissue() {
        server.enqueue(
            MockResponse()
                .setResponseCode(400)
                .setHeader("Content-Type", "application/problem+json")
                .setBody(
                    """{"type":"https://docs.dle.dev/problems/claim-code-invalid","title":"Claim code invalid","status":400,"reason":"expired","can_reissue":true,"errors":{"claim_code":["expired"]}}""",
                ),
        )
        val api = api()

        val failure = assertThrows<DleException.Http> { runBlocking { api.resolve(contractRequest(api)) } }

        assertEquals(400, failure.status)
        assertTrue(failure.isClaimCodeInvalid)
        assertFalse(failure.isRetriable)
        assertEquals("expired", failure.problem?.reason)
        assertEquals(true, failure.problem?.canReissue)
    }

    @Test
    fun unauthorized_isNotRetriable() {
        server.enqueue(MockResponse().setResponseCode(401).setBody(""))
        val api = api()

        val failure = assertThrows<DleException.Http> { runBlocking { api.resolve(contractRequest(api)) } }

        assertTrue(failure.isUnauthorized)
        assertFalse(failure.isRetriable)
        assertNull(failure.problem)
        assertNull(failure.retryAfterMillis)
        assertEquals("HTTP 401", failure.message)
    }

    @Test
    fun serverError_isRetriable() {
        server.enqueue(MockResponse().setResponseCode(503).setBody("<html>gateway</html>"))
        val api = api()

        val failure = assertThrows<DleException.Http> { runBlocking { api.resolve(contractRequest(api)) } }

        assertEquals(503, failure.status)
        assertTrue(failure.isRetriable)
        assertNull(failure.problem, "an HTML error page is not a problem document")
    }

    @Test
    fun malformedSuccessBody_isRefusedNotGuessed() {
        server.enqueue(json(200, """{"matched":true,"match_type":"telepathy","confidence":1.0}"""))
        val api = api()

        assertThrows<DleException.Malformed> { runBlocking { api.resolve(contractRequest(api)) } }
    }

    @Test
    fun emptySuccessBody_isMalformed() {
        server.enqueue(MockResponse().setResponseCode(200).setBody(""))
        val api = api()

        assertThrows<DleException.Malformed> { runBlocking { api.resolve(contractRequest(api)) } }
    }

    @Test
    fun redirects_areNotFollowed() {
        server.enqueue(MockResponse().setResponseCode(307).setHeader("Location", "http://127.0.0.1:${server.port}/elsewhere"))
        val api = api()

        val failure = assertThrows<DleException.Http> { runBlocking { api.resolve(contractRequest(api)) } }

        assertEquals(307, failure.status)
        assertEquals(1, server.requestCount, "the bearer key must not be replayed to a redirect target")
    }

    @Test
    fun connectionFailure_isANetworkException() {
        // A second server, started and stopped here, leaves a port nobody listens on.
        val dead = MockWebServer()
        dead.start()
        val port = dead.port
        dead.shutdown()
        val config = DleConfig.builder("http://127.0.0.1:$port", "dle_pk_test").logLevel(DleLogLevel.OFF).build()
        val api = api(config)

        val failure = assertThrows<DleException> { runBlocking { api.resolve(contractRequest(api)) } }

        assertTrue(failure is DleException.Network || failure is DleException.Timeout)
        assertTrue(failure.isRetriable)
    }

    @Test
    fun sendEvents_postsTheContractBatchAndReadsTheCounters() {
        server.enqueue(MockResponse().setResponseCode(202).setHeader("Content-Type", "application/json").setBody("""{"accepted":2,"rejected":0}"""))
        val api = api()
        val events = listOf(
            DleEvent.linkOpen("https://link.zak.sk/aB3xK9pQ", CONTRACT_MOMENT_MILLIS),
            DleEvent(type = DleEventType.CONVERSION, name = "purchase", value = 24.9, currency = "EUR", timestampMillis = CONTRACT_MOMENT_MILLIS + 300_000L),
        )
        val batch = EventBatch(installId = CONTRACT_INSTALL_ID, events = events.map { it.toWire(0L) })

        val accepted = runBlocking { api.sendEvents(batch) }

        val recorded = server.takeRequest()
        assertEquals("/v1/events", recorded.path)
        assertEquals("Bearer dle_pk_test", recorded.getHeader("Authorization"))
        assertEquals(WireModelsTest.EVENT_BATCH_LITERAL, recorded.body.readUtf8())
        assertEquals(2, accepted.accepted)
        assertEquals(0, accepted.rejected)
    }

    @Test
    fun sendEvents_acceptsAnEmpty202Body() {
        server.enqueue(MockResponse().setResponseCode(202).setBody(""))
        val api = api()

        val accepted = runBlocking { api.sendEvents(EventBatch(installId = "a", events = listOf(DleEvent.session().toWire(0L)))) }

        assertEquals(0, accepted.accepted)
    }

    @Test
    fun sendEvents_refusesAnOversizedBatchLocally() {
        val api = api()
        val events = List(101) { DleEvent.session().toWire(0L) }

        assertThrows<DleException.InvalidArgument> { runBlocking { api.sendEvents(EventBatch(installId = "a", events = events)) } }
        assertEquals(0, server.requestCount)
    }

    @Test
    fun retryAfter_parsesSecondsAndHttpDates() {
        assertEquals(30_000L, DleApi.parseRetryAfter("30"))
        assertEquals(0L, DleApi.parseRetryAfter("0"))
        assertEquals(30_000L, DleApi.parseRetryAfter("Thu, 03 Sep 2026 10:00:30 GMT", nowMillis = CONTRACT_MOMENT_MILLIS))
        assertEquals(0L, DleApi.parseRetryAfter("Thu, 03 Sep 2026 09:00:00 GMT", nowMillis = CONTRACT_MOMENT_MILLIS), "a date in the past means now")
        assertNull(DleApi.parseRetryAfter(null))
        assertNull(DleApi.parseRetryAfter(""))
        assertNull(DleApi.parseRetryAfter("soon"))
    }

    private fun json(status: Int, body: String): MockResponse =
        MockResponse().setResponseCode(status).setHeader("Content-Type", "application/json").setBody(body)
}
