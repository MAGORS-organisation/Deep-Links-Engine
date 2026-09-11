package sk.magors.dle.internal

import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.SerializationException
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import sk.magors.dle.DeferredLink
import sk.magors.dle.DleConfig
import sk.magors.dle.DleConsent
import sk.magors.dle.DleException
import sk.magors.dle.ResolveOptions
import java.io.IOException
import java.io.InterruptedIOException
import java.net.SocketTimeoutException
import java.text.ParseException
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * The HTTP client of the SDK plane: `POST /v1/resolve` and `POST /v1/events`.
 *
 * Every request carries `Authorization: Bearer <sdkKey>`, `Content-Type: application/json` and a
 * fresh W3C `traceparent` (NFR-12). Redirects are disabled on the derived [OkHttpClient]: a bearer
 * key must never be replayed to wherever a misconfigured proxy points. Non success answers are
 * turned into [DleException.Http] with the RFC 9457 problem document parsed and `Retry-After`
 * honoured; a 2xx body the SDK cannot use is [DleException.Malformed].
 *
 * Privacy rule enforced here and nowhere else: the `signals` object is attached to a resolve
 * request **only** when the operator opted in ([DleConfig.probabilisticSignals]) **and** the user
 * consented ([DleConsent.allowsDeviceSignals]). Otherwise the member is absent, not empty
 * (TC-145, TC-146).
 *
 * @param config the SDK configuration.
 * @param signalsProvider collects the coarse device signals on demand. Invoked only when both
 * gates above are open, so a caller that never consents never has signals computed at all.
 * @param log the SDK log; status codes only, never a body, key or URL query.
 * @param baseClient the client to derive from, see [DleConfig.okHttpClient].
 */
internal class DleApi(
    private val config: DleConfig,
    private val signalsProvider: () -> SignalsWire?,
    private val log: SdkLog,
    baseClient: OkHttpClient? = config.okHttpClient,
) {
    private val client: OkHttpClient = (baseClient?.newBuilder() ?: OkHttpClient.Builder())
        .connectTimeout(config.connectTimeoutMillis, TimeUnit.MILLISECONDS)
        .readTimeout(config.readTimeoutMillis, TimeUnit.MILLISECONDS)
        .writeTimeout(config.readTimeoutMillis, TimeUnit.MILLISECONDS)
        .callTimeout(config.connectTimeoutMillis + config.readTimeoutMillis, TimeUnit.MILLISECONDS)
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(false)
        .build()

    /**
     * Builds the `POST /v1/resolve` body. Separate from [resolve] so the consent gate is testable
     * without a server.
     *
     * @param installId the persisted installation identifier.
     * @param appVersion host application version.
     * @param osVersion `Build.VERSION.RELEASE`.
     * @param referrer the raw Install Referrer, already clipped; `null` when unavailable.
     * @param options explicit evidence from the application; the claim code is already normalised.
     * @param consent the consent state at call time.
     */
    fun resolveRequest(
        installId: String,
        appVersion: String?,
        osVersion: String?,
        referrer: String?,
        options: ResolveOptions,
        consent: DleConsent,
    ): ResolveRequest {
        val signals = if (config.probabilisticSignals && consent.allowsDeviceSignals) signalsProvider() else null
        return ResolveRequest(
            installId = installId,
            platform = PLATFORM_ANDROID,
            appVersion = appVersion,
            osVersion = osVersion,
            referrer = InstallReferrerParser.clip(referrer),
            claimCode = options.claimCode?.takeIf { it.isNotBlank() },
            loginKey = options.loginKey?.takeIf { it.isNotBlank() },
            signals = signals,
            consent = consent.toWire(),
        )
    }

    /**
     * `POST /v1/resolve`.
     *
     * @return the parsed and validated answer.
     * @throws DleException on any failure; see the class documentation for the mapping.
     */
    suspend fun resolve(request: ResolveRequest): DeferredLink {
        val body = DleJson.encodeToString(ResolveRequest.serializer(), request)
        val text = post(RESOLVE_PATH, body)
        val response = try {
            DleJson.decodeFromString(ResolveResponse.serializer(), text)
        } catch (e: SerializationException) {
            throw DleException.Malformed("resolve response is not a ResolveResponseDto", e)
        } catch (e: IllegalArgumentException) {
            throw DleException.Malformed("resolve response is not a ResolveResponseDto", e)
        }
        return response.toDeferredLink()
    }

    /**
     * `POST /v1/events`. The server answers 202 with the accepted and rejected counters.
     *
     * @throws DleException on any failure. [DleException.isRetriable] tells the queue whether to
     * keep the batch.
     */
    suspend fun sendEvents(batch: EventBatch): EventBatchAccepted {
        if (batch.events.isEmpty() || batch.events.size > MAX_EVENTS_PER_BATCH) {
            throw DleException.InvalidArgument("a batch carries 1 to $MAX_EVENTS_PER_BATCH events, not ${batch.events.size}")
        }
        val body = DleJson.encodeToString(EventBatch.serializer(), batch)
        val text = post(EVENTS_PATH, body)
        if (text.isBlank()) return EventBatchAccepted()
        return try {
            DleJson.decodeFromString(EventBatchAccepted.serializer(), text)
        } catch (_: SerializationException) {
            // The batch was accepted; a counter the SDK cannot read changes nothing.
            EventBatchAccepted()
        } catch (_: IllegalArgumentException) {
            EventBatchAccepted()
        }
    }

    /** Sends one request and returns the body of a 2xx answer. */
    private suspend fun post(path: String, json: String): String {
        // Bytes, not a String body: OkHttp appends "; charset=utf-8" to a String body's media type
        // and the wire contract names the type as exactly `application/json`.
        val request = Request.Builder()
            .url(config.endpoint + path)
            .post(json.toByteArray(Charsets.UTF_8).toRequestBody(JSON_MEDIA_TYPE))
            .header("Authorization", "Bearer " + config.sdkKey)
            .header("Content-Type", "application/json")
            .header("Accept", "application/json, application/problem+json")
            .header("traceparent", TraceContext.newTraceparent())
            .header("User-Agent", USER_AGENT)
            .build()
        val response = execute(client.newCall(request))
        response.use { r ->
            val text = try {
                r.body?.string() ?: ""
            } catch (e: IOException) {
                throw DleException.Network("reading the response failed", e)
            }
            log.debug("POST $path -> ${r.code}")
            if (r.isSuccessful) {
                if (text.isBlank() && path == RESOLVE_PATH) {
                    throw DleException.Malformed("HTTP ${r.code} without a JSON body")
                }
                return text
            }
            throw httpFailure(r, text)
        }
    }

    private fun httpFailure(response: Response, text: String): DleException.Http {
        val problem = parseProblem(text)?.toProblemDocument()
        val retryAfter = parseRetryAfter(response.header("Retry-After"))
        return DleException.Http(status = response.code, problem = problem, retryAfterMillis = retryAfter)
    }

    private suspend fun execute(call: Call): Response = suspendCancellableCoroutine { continuation ->
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(
            object : Callback {
                override fun onFailure(call: Call, e: IOException) {
                    if (continuation.isCancelled) return
                    val failure: DleException = when {
                        e is SocketTimeoutException || e is InterruptedIOException && e.message == "timeout" ->
                            DleException.Timeout("request to ${call.request().url.encodedPath} timed out", e)
                        else -> DleException.Network("request to ${call.request().url.encodedPath} failed", e)
                    }
                    continuation.resumeWithException(failure)
                }

                override fun onResponse(call: Call, response: Response) {
                    if (continuation.isCancelled) {
                        response.close()
                        return
                    }
                    continuation.resume(response)
                }
            },
        )
    }

    internal companion object {
        const val RESOLVE_PATH: String = "/v1/resolve"
        const val EVENTS_PATH: String = "/v1/events"
        const val USER_AGENT: String = "dle-android/" + sk.magors.dle.BuildConfig.SDK_VERSION

        private val JSON_MEDIA_TYPE = "application/json".toMediaType()

        /** Parses a problem document body; anything that is not one yields `null`. */
        fun parseProblem(text: String): ProblemWire? {
            if (text.isBlank()) return null
            return try {
                DleJson.decodeFromString(ProblemWire.serializer(), text)
            } catch (_: SerializationException) {
                null
            } catch (_: IllegalArgumentException) {
                null
            }
        }

        /**
         * Parses `Retry-After` (delay-seconds or an HTTP-date) into milliseconds.
         *
         * @param header the raw header value, `null` when absent.
         * @param nowMillis the current time, for the HTTP-date form.
         * @return the delay, or `null` when the header is absent or unreadable.
         */
        fun parseRetryAfter(header: String?, nowMillis: Long = System.currentTimeMillis()): Long? {
            val trimmed = header?.trim().orEmpty()
            if (trimmed.isEmpty()) return null
            if (trimmed.all { it in '0'..'9' }) {
                return trimmed.toLongOrNull()?.let { it * 1000L }
            }
            val format = SimpleDateFormat("EEE, dd MMM yyyy HH:mm:ss zzz", Locale.US)
            format.timeZone = TimeZone.getTimeZone("GMT")
            return try {
                val at = format.parse(trimmed)?.time ?: return null
                (at - nowMillis).coerceAtLeast(0L)
            } catch (_: ParseException) {
                null
            }
        }
    }
}
