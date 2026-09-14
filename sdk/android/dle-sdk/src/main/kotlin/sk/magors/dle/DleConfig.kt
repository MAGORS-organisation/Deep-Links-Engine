package sk.magors.dle

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient

/**
 * Configuration handed to [Dle.initialize]. Build it with [Builder] (Java) or [dleConfig]
 * (Kotlin). Every value is validated once, in [Builder.build], so a wrong endpoint fails at start
 * up rather than at the first resolve.
 *
 * @property endpoint base URL of the DLE SDK plane (`dle-control`, default port 8081), without a
 * trailing slash. `https` is required except for loopback and the emulator host `10.0.2.2`: the
 * SDK key travels as a bearer token and must not cross the network in the clear.
 * @property sdkKey the publishable SDK key, sent as `Authorization: Bearer <sdkKey>`. It ships
 * inside the binary by design and is not a secret; what protects data is the server's
 * authorisation and rate limits. The SDK never logs it.
 * @property consent the consent state that applies until [DleClient.setConsent] is called and no
 * recorded decision exists. Default: [DleConsent.denied].
 * @property autoReportOpens whether the SDK observes activity lifecycle callbacks and reports
 * App Link opens on its own (FR-223). Default `true`. With `false`, call
 * [DleClient.handleIntent] from `onCreate` and `onNewIntent` yourself.
 * @property probabilisticSignals opt in to sending coarse device signals (language, screen size,
 * UTC offset, device model) so the engine may attempt a probabilistic match. Off by default,
 * and additionally gated on [DleConsent.attribution] at call time. Never a fingerprint hash
 * (spec §0.2, §A.6, FR-227).
 * @property requireAnalyticsConsentForEvents whether behavioural events are dropped without
 * [DleConsent.analytics]. Default `true`.
 * @property connectTimeoutMillis TCP connect timeout. Default 5000.
 * @property readTimeoutMillis time allowed for the server to answer. Default 10000.
 * @property referrerTimeoutMillis how long a resolve waits for the Play Install Referrer before
 * proceeding without it. Default 8000. The SDK retries later when the referrer arrives after a
 * `none` answer.
 * @property flushDelayMillis debounce before an automatic event flush. Default 3000; keeps the SDK
 * inside the documented `/v1/events` budget of sixty events a minute (spec §E.9).
 * @property maxQueueSize maximum number of buffered events before the oldest are dropped.
 * Default 500.
 * @property logLevel verbosity. Default [DleLogLevel.WARN].
 * @property logger destination for log lines. Default `android.util.Log`.
 * @property appVersion host application version reported as `app_version`. Default: read from
 * the package manager.
 * @property okHttpClient an `OkHttpClient` to share with the host application. The SDK derives its
 * own instance from it with redirects disabled and its timeouts applied. Default: a private
 * client. Certificate pinning is the place to configure here, and it is deliberately optional
 * (spec §E.7 rule 6): pinning without a rotation plan is an outage fixed by an app release.
 */
public class DleConfig private constructor(builder: Builder) {
    public val endpoint: String = builder.normalisedEndpoint
    public val sdkKey: String = builder.sdkKey.trim()
    public val consent: DleConsent = builder.consent
    public val autoReportOpens: Boolean = builder.autoReportOpens
    public val probabilisticSignals: Boolean = builder.probabilisticSignals
    public val requireAnalyticsConsentForEvents: Boolean = builder.requireAnalyticsConsentForEvents
    public val connectTimeoutMillis: Long = builder.connectTimeoutMillis
    public val readTimeoutMillis: Long = builder.readTimeoutMillis
    public val referrerTimeoutMillis: Long = builder.referrerTimeoutMillis
    public val flushDelayMillis: Long = builder.flushDelayMillis
    public val maxQueueSize: Int = builder.maxQueueSize
    public val logLevel: DleLogLevel = builder.logLevel
    public val logger: DleLogger? = builder.logger
    public val appVersion: String? = builder.appVersion
    public val okHttpClient: OkHttpClient? = builder.okHttpClient

    override fun toString(): String =
        "DleConfig(endpoint=$endpoint, sdkKey=<redacted>, autoReportOpens=$autoReportOpens, " +
            "probabilisticSignals=$probabilisticSignals, logLevel=$logLevel)"

    /**
     * Fluent builder, usable from Java.
     *
     * @param endpoint see [DleConfig.endpoint].
     * @param sdkKey see [DleConfig.sdkKey].
     */
    public class Builder(
        private val endpoint: String,
        internal val sdkKey: String,
    ) {
        internal var consent: DleConsent = DleConsent.denied()
        internal var autoReportOpens: Boolean = true
        internal var probabilisticSignals: Boolean = false
        internal var requireAnalyticsConsentForEvents: Boolean = true
        internal var connectTimeoutMillis: Long = DEFAULT_CONNECT_TIMEOUT_MILLIS
        internal var readTimeoutMillis: Long = DEFAULT_READ_TIMEOUT_MILLIS
        internal var referrerTimeoutMillis: Long = DEFAULT_REFERRER_TIMEOUT_MILLIS
        internal var flushDelayMillis: Long = DEFAULT_FLUSH_DELAY_MILLIS
        internal var maxQueueSize: Int = DEFAULT_MAX_QUEUE_SIZE
        internal var logLevel: DleLogLevel = DleLogLevel.WARN
        internal var logger: DleLogger? = null
        internal var appVersion: String? = null
        internal var okHttpClient: OkHttpClient? = null
        internal lateinit var normalisedEndpoint: String

        /** See [DleConfig.consent]. */
        public fun consent(value: DleConsent): Builder = apply { consent = value }

        /** See [DleConfig.autoReportOpens]. */
        public fun autoReportOpens(value: Boolean): Builder = apply { autoReportOpens = value }

        /** See [DleConfig.probabilisticSignals]. */
        public fun probabilisticSignals(value: Boolean): Builder = apply { probabilisticSignals = value }

        /** See [DleConfig.requireAnalyticsConsentForEvents]. */
        public fun requireAnalyticsConsentForEvents(value: Boolean): Builder =
            apply { requireAnalyticsConsentForEvents = value }

        /** See [DleConfig.connectTimeoutMillis]. */
        public fun connectTimeoutMillis(value: Long): Builder = apply { connectTimeoutMillis = value }

        /** See [DleConfig.readTimeoutMillis]. */
        public fun readTimeoutMillis(value: Long): Builder = apply { readTimeoutMillis = value }

        /** See [DleConfig.referrerTimeoutMillis]. */
        public fun referrerTimeoutMillis(value: Long): Builder = apply { referrerTimeoutMillis = value }

        /** See [DleConfig.flushDelayMillis]. */
        public fun flushDelayMillis(value: Long): Builder = apply { flushDelayMillis = value }

        /** See [DleConfig.maxQueueSize]. */
        public fun maxQueueSize(value: Int): Builder = apply { maxQueueSize = value }

        /** See [DleConfig.logLevel]. */
        public fun logLevel(value: DleLogLevel): Builder = apply { logLevel = value }

        /** See [DleConfig.logger]. */
        public fun logger(value: DleLogger?): Builder = apply { logger = value }

        /** See [DleConfig.appVersion]. */
        public fun appVersion(value: String?): Builder = apply { appVersion = value }

        /** See [DleConfig.okHttpClient]. */
        public fun okHttpClient(value: OkHttpClient?): Builder = apply { okHttpClient = value }

        /**
         * Validates and builds.
         *
         * @throws DleException.Configuration when a value is unusable.
         */
        public fun build(): DleConfig {
            normalisedEndpoint = normaliseEndpoint(endpoint)
            if (sdkKey.isBlank()) throw DleException.Configuration("sdkKey must not be blank")
            if (connectTimeoutMillis <= 0 || readTimeoutMillis <= 0) {
                throw DleException.Configuration("timeouts must be positive")
            }
            if (referrerTimeoutMillis < 0) throw DleException.Configuration("referrerTimeoutMillis must not be negative")
            if (flushDelayMillis < 0) throw DleException.Configuration("flushDelayMillis must not be negative")
            if (maxQueueSize < 1) throw DleException.Configuration("maxQueueSize must be at least 1")
            return DleConfig(this)
        }

        private fun normaliseEndpoint(raw: String): String {
            val url = raw.trim().toHttpUrlOrNull()
                ?: throw DleException.Configuration("endpoint must be an absolute http(s) URL")
            if (url.scheme != "https" && !isLocalHost(url.host)) {
                throw DleException.Configuration("endpoint must use https (plain http is allowed only for localhost and 10.0.2.2)")
            }
            if (!url.query.isNullOrEmpty() || url.fragment != null) {
                throw DleException.Configuration("endpoint must not carry a query string or fragment")
            }
            return url.toString().trimEnd('/')
        }

        private fun isLocalHost(host: String): Boolean =
            host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "10.0.2.2"
    }

    public companion object {
        /** Default [connectTimeoutMillis]. */
        public const val DEFAULT_CONNECT_TIMEOUT_MILLIS: Long = 5_000L

        /** Default [readTimeoutMillis]. */
        public const val DEFAULT_READ_TIMEOUT_MILLIS: Long = 10_000L

        /** Default [referrerTimeoutMillis]. */
        public const val DEFAULT_REFERRER_TIMEOUT_MILLIS: Long = 8_000L

        /** Default [flushDelayMillis]. */
        public const val DEFAULT_FLUSH_DELAY_MILLIS: Long = 3_000L

        /** Default [maxQueueSize]. */
        public const val DEFAULT_MAX_QUEUE_SIZE: Int = 500

        /**
         * Starts a builder.
         *
         * @param endpoint see [DleConfig.endpoint].
         * @param sdkKey see [DleConfig.sdkKey].
         */
        @JvmStatic
        public fun builder(endpoint: String, sdkKey: String): Builder = Builder(endpoint, sdkKey)
    }
}

/**
 * Kotlin shorthand for [DleConfig.Builder].
 *
 * ```
 * Dle.initialize(this, dleConfig("https://links.example.sk", "dle_…") {
 *     consent(DleConsent.denied())
 *     logLevel(DleLogLevel.INFO)
 * })
 * ```
 *
 * @param endpoint see [DleConfig.endpoint].
 * @param sdkKey see [DleConfig.sdkKey].
 * @param configure builder customisation.
 */
public inline fun dleConfig(
    endpoint: String,
    sdkKey: String,
    configure: DleConfig.Builder.() -> Unit = {},
): DleConfig = DleConfig.Builder(endpoint, sdkKey).apply(configure).build()
