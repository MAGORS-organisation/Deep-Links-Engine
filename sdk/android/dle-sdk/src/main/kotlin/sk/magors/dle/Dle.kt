package sk.magors.dle

import android.app.Activity
import android.app.Application
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import sk.magors.dle.internal.AppLinkHandler
import sk.magors.dle.internal.ClaimCode
import sk.magors.dle.internal.Clock
import sk.magors.dle.internal.DleApi
import sk.magors.dle.internal.EventBatch
import sk.magors.dle.internal.EventQueue
import sk.magors.dle.internal.InstallIdStore
import sk.magors.dle.internal.InstallReferrerReader
import sk.magors.dle.internal.PLATFORM_ANDROID
import sk.magors.dle.internal.ResolveLinkRequest
import sk.magors.dle.internal.SdkLog
import sk.magors.dle.internal.SignalsWire
import sk.magors.dle.internal.StateStore
import java.io.File
import java.io.IOException
import java.util.Locale
import java.util.TimeZone

/**
 * The Deep Link Engine Android SDK.
 *
 * ```
 * // Application.onCreate
 * Dle.initialize(this, dleConfig("https://links.example.sk", "dle_pk_…"))
 *
 * // First screen. Every later launch gets the stored answer back, so route on a match once.
 * lifecycleScope.launch {
 *     val delivered = Dle.shared.cachedLink?.matched == true
 *     val link = Dle.resolve()
 *     if (!delivered && link.isDeterministic) navigateTo(link.link?.deeplinkPath)
 * }
 * ```
 *
 * The process wide entry point. Every call forwards to the [DleClient] created by [initialize];
 * hold on to that client instead when you prefer injecting it. The SDK reads no clipboard, asks
 * for no advertising identifier and computes no fingerprint (FR-227, spec §E.7): its inputs are
 * the Play Install Referrer, App Link opens, and the evidence the application hands it.
 */
public object Dle {
    /** Version of this SDK, as reported in the `User-Agent` of every request. */
    public const val VERSION: String = BuildConfig.SDK_VERSION

    @Volatile
    private var client: DleClient? = null

    /**
     * Creates the shared client. Call it once, from `Application.onCreate`. Calling it again
     * returns the existing client and ignores the new configuration.
     *
     * @param context any context; the application context is retained.
     * @param config the configuration, built with [DleConfig.Builder] or [dleConfig].
     * @return the shared client.
     */
    @JvmStatic
    public fun initialize(context: Context, config: DleConfig): DleClient {
        client?.let { existing ->
            existing.log.warn("Dle.initialize called twice; the first configuration stays in effect")
            return existing
        }
        synchronized(this) {
            client?.let { return it }
            val created = DleClient.create(context.applicationContext, config)
            client = created
            return created
        }
    }

    /** Whether [initialize] has been called. */
    @JvmStatic
    public val isInitialized: Boolean
        get() = client != null

    /**
     * The shared client.
     *
     * @throws DleException.Configuration before [initialize] was called.
     */
    @JvmStatic
    public val shared: DleClient
        get() = client ?: throw DleException.Configuration("Dle.initialize(context, config) must be called first")

    /** See [DleClient.installId]. */
    @JvmStatic
    public val installId: String
        get() = shared.installId

    /** See [DleClient.resolve]. */
    public suspend fun resolve(options: ResolveOptions = ResolveOptions.NONE): DeferredLink = shared.resolve(options)

    /** See [DleClient.resolve]. */
    @JvmStatic
    public fun resolve(options: ResolveOptions, callback: DleCallback<DeferredLink>) {
        shared.resolve(options, callback)
    }

    /** See [DleClient.resolve]. */
    @JvmStatic
    public fun resolve(callback: DleCallback<DeferredLink>) {
        shared.resolve(ResolveOptions.NONE, callback)
    }

    /** See [DleClient.trackEvent]. */
    @JvmStatic
    public fun trackEvent(event: DleEvent) {
        shared.trackEvent(event)
    }

    /** See [DleClient.handleIntent]. */
    @JvmStatic
    public fun handleIntent(intent: Intent?): AppLinkOpen? = shared.handleIntent(intent)

    /** See [DleClient.resolveLink]. */
    public suspend fun resolveLink(open: AppLinkOpen): DeferredLink = shared.resolveLink(open)

    /** See [DleClient.resolveLink]. */
    @JvmStatic
    public fun resolveLink(open: AppLinkOpen, callback: DleCallback<DeferredLink>) {
        shared.resolveLink(open, callback)
    }

    /** See [DleClient.setConsent]. */
    @JvmStatic
    public fun setConsent(consent: DleConsent) {
        shared.setConsent(consent)
    }

    /** See [DleClient.flush]. */
    @JvmStatic
    public fun flush() {
        shared.flush()
    }
}

/**
 * The SDK proper. Obtain it from [Dle.initialize]; everything on [Dle] forwards here.
 *
 * Threading: every method may be called from any thread. Suspending functions do their I/O on
 * [Dispatchers.IO]; [DleCallback] results are delivered on the main thread.
 *
 * @property config the configuration in effect.
 */
public class DleClient internal constructor(
    private val application: Context,
    public val config: DleConfig,
    /** Host application version as sent in `app_version`. */
    public val appVersion: String?,
    internal val log: SdkLog,
    private val installIdStore: InstallIdStore,
    private val state: StateStore,
    private val api: DleApi,
    private val queue: EventQueue,
    private val referrerReader: InstallReferrerReader,
    private val scope: CoroutineScope,
    private val clock: Clock,
) {
    private val appLinkHandler = AppLinkHandler(enqueue = { enqueueGated(it) }, log = log)
    private val resolveLock = Mutex()
    private val mainHandler: Handler? = try {
        Looper.getMainLooper()?.let { Handler(it) }
    } catch (_: RuntimeException) {
        null
    }

    /**
     * The identifier of this installation: a random UUID minted once, persisted, never derived
     * from the device. Hand it to your backend when you call the engine's deletion endpoint on a
     * user's behalf (spec §E.6.3).
     */
    public val installId: String
        get() = installIdStore.getOrCreate()

    /**
     * The consent in effect: the recorded decision when there is one, otherwise
     * [DleConfig.consent].
     */
    @Volatile
    public var consent: DleConsent = state.consent() ?: config.consent
        private set

    /** The cached resolve answer, without any request. `null` until the first resolve completes. */
    public val cachedLink: DeferredLink?
        get() = state.cachedResolve()?.link

    private val osVersion: String? = Build.VERSION.RELEASE?.takeIf { it.isNotBlank() }

    /**
     * Recovers the click that led to this installation (`POST /v1/resolve`, spec §B.7.2).
     *
     * The first call reads the Play Install Referrer once (S1), sends it with the platform,
     * versions, consent and any evidence in [options], and persists the answer. Later calls
     * return that answer without a request (TC-143), with two exceptions: a non final answer
     * whose [DeferredLink.expiresIn] has elapsed, and a call carrying new evidence
     * ([ResolveOptions.hasExplicitEvidence]) while the stored answer is not deterministic. An
     * organic installation is not a failure: the engine answers [MatchType.NONE] (TC-142).
     * Because a match comes back on every later call, route on it only when [cachedLink] was
     * not already a match before the call.
     *
     * @param options extra evidence: a claim code the user typed (S3) or a login key (S2).
     * @return the answer. Check [DeferredLink.isDeterministic] before acting irreversibly.
     * @throws DleException.InvalidArgument for a malformed claim code.
     * @throws DleException on network, timeout, HTTP and malformed body failures. The cached
     * answer, when there is one, stays available through [cachedLink].
     */
    public suspend fun resolve(options: ResolveOptions = ResolveOptions.NONE): DeferredLink {
        val evidence = normalise(options)
        return resolveLock.withLock {
            val cached = state.cachedResolve()
            if (cached != null && !cached.needsRequest(evidence, clock.nowMillis())) {
                log.debug("resolve answered from cache: ${cached.link.matchType.wireName}")
                afterResolve()
                return@withLock cached.link
            }
            val link = withContext(Dispatchers.IO) {
                // Without attribution consent the engine links nothing, so the referrer is not
                // even read: it is read when a resolve with consent goes out.
                val referrer = if (consent.attribution) referrerReader.readOnce().referrer else null
                val request = api.resolveRequest(
                    installId = installId,
                    appVersion = appVersion,
                    osVersion = osVersion,
                    referrer = referrer,
                    options = evidence,
                    consent = consent,
                )
                api.resolve(request)
            }
            state.saveResolve(link, clock.nowMillis())
            log.info("resolved: match_type=${link.matchType.wireName} confidence=${link.confidence}")
            afterResolve()
            link
        }
    }

    /**
     * Java friendly [resolve]. Exactly one callback method is invoked, on the main thread.
     *
     * @param options extra evidence; [ResolveOptions.NONE] for the plain first launch resolve.
     * @param callback receives the answer or the failure.
     */
    @JvmOverloads
    public fun resolve(options: ResolveOptions = ResolveOptions.NONE, callback: DleCallback<DeferredLink>) {
        scope.launch {
            val outcome = try {
                Result.success(resolve(options))
            } catch (e: DleException) {
                Result.failure(e)
            } catch (e: RuntimeException) {
                Result.failure(DleException.Internal("resolve failed unexpectedly", e))
            }
            onMain {
                outcome.fold(
                    onSuccess = { callback.onSuccess(it) },
                    onFailure = { callback.onFailure(it as DleException) },
                )
            }
        }
    }

    /**
     * Buffers an event for `POST /v1/events` (FR-226). Events are batched and delivered
     * [DleConfig.flushDelayMillis] later, survive process death and are retried offline.
     *
     * Behavioural events are dropped here, not buffered, while [DleConsent.analytics] is `false`
     * and [DleConfig.requireAnalyticsConsentForEvents] is on. [DleEventType.LINK_OPEN] is exempt
     * (FR-223). Never throws.
     *
     * @param event the event, built with the factories on [DleEvent].
     */
    public fun trackEvent(event: DleEvent) {
        enqueueGated(event)
    }

    /** Alias of [trackEvent]. */
    public fun track(event: DleEvent) {
        enqueueGated(event)
    }

    /**
     * Reports an App Link open (FR-223). With [DleConfig.autoReportOpens] the SDK calls this for
     * every resumed activity; call it yourself from `onCreate` and `onNewIntent` otherwise, and
     * in any case call `setIntent(intent)` in `onNewIntent` so the resumed activity carries the
     * new link. Each intent is reported once however often it is handed in.
     *
     * The returned [AppLinkOpen.url] is untrusted input even though the operating system
     * delivered it: validate it with a [DeepLinkAllowlist] before navigating (spec §E.7).
     *
     * @param intent the activity's intent, or `null`.
     * @return the open, or `null` when the intent does not carry an `http(s)` link.
     */
    public fun handleIntent(intent: Intent?): AppLinkOpen? = appLinkHandler.handle(intent)

    /**
     * Resolves the screen a short link the app was opened with should lead to
     * (`POST /v1/resolve-link`, FR-223, spec §B.6.4).
     *
     * A verified App Link opens the app with the short URL itself — `https://go.example.com/AbC12345`
     * — which does not say which screen to show. This asks the engine: the answer has
     * [MatchType.DIRECT_OPEN], confidence 1.0 and the link's [DeferredLink.deeplinkPath] after the
     * link's routing rules were applied for Android. Call it with the [AppLinkOpen] that
     * [handleIntent] returned; [handleIntent] already reported the open, and this call records
     * nothing, so an open is never counted twice.
     *
     * @param open the open returned by [handleIntent].
     * @return the answer; `matched` is `false` when the URL names no link of this app's tenant.
     * @throws DleException on network, timeout, HTTP and malformed body failures.
     */
    public suspend fun resolveLink(open: AppLinkOpen): DeferredLink = withContext(Dispatchers.IO) {
        api.resolveLink(
            ResolveLinkRequest(
                url = open.reportedUrl,
                installId = installId,
                platform = PLATFORM_ANDROID,
                appVersion = appVersion,
            ),
        )
    }

    /**
     * Java friendly [resolveLink]. Exactly one callback method is invoked, on the main thread.
     *
     * @param open the open returned by [handleIntent].
     * @param callback receives the answer or the failure.
     */
    public fun resolveLink(open: AppLinkOpen, callback: DleCallback<DeferredLink>) {
        scope.launch {
            val outcome = try {
                Result.success(resolveLink(open))
            } catch (e: DleException) {
                Result.failure(e)
            } catch (e: RuntimeException) {
                Result.failure(DleException.Internal("resolveLink failed unexpectedly", e))
            }
            onMain {
                outcome.fold(
                    onSuccess = { callback.onSuccess(it) },
                    onFailure = { callback.onFailure(it as DleException) },
                )
            }
        }
    }

    /**
     * Records the user's decision (spec §E.6.2). The decision is persisted, sent with the next
     * resolve request as its `consent` member, and gates device signals and behavioural events
     * from now on. It never triggers a new resolve on its own: a final answer stays final.
     *
     * @param consent the decision. A missing [DleConsent.timestampMillis] is stamped with now.
     */
    public fun setConsent(consent: DleConsent) {
        val stamped = if (consent.timestampMillis == null) consent.copy(timestampMillis = clock.nowMillis()) else consent
        val previous = this.consent
        state.saveConsent(stamped)
        this.consent = stamped
        log.info("consent recorded: analytics=${stamped.analytics} attribution=${stamped.attribution}")
        if (stamped.attribution && !previous.attribution) forgetUnmatchedAnswer()
        if (stamped.analytics && state.isResolveDone) emitFirstOpenOnce()
    }

    /**
     * A resolve made before attribution consent was recorded answers `none` with reason
     * `consent_missing` and a non-final `expires_in`: the engine could not link the click, not
     * because there was none. Once consent arrives that answer is stale, so it is dropped and the
     * next [resolve] - the next launch, in the documented integration - asks again (TC-141).
     */
    private fun forgetUnmatchedAnswer() {
        val cached = state.cachedResolve() ?: return
        if (cached.link.matched || cached.link.isFinal) return
        state.clearResolve()
        log.info("cached unmatched answer dropped: attribution consent was recorded after it")
    }

    /** Sends every buffered event now instead of waiting for the flush delay. Never throws. */
    public fun flush() {
        queue.flush()
    }

    // -------------------------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------------------------

    private fun normalise(options: ResolveOptions): ResolveOptions {
        val raw = options.claimCode ?: return options
        val code = ClaimCode.normalize(raw)
        if (!ClaimCode.isWellFormed(code)) {
            throw DleException.InvalidArgument(
                "claim code must be ${ClaimCode.LENGTH} characters from the alphabet ${ClaimCode.ALPHABET}",
            )
        }
        return ResolveOptions(claimCode = code, loginKey = options.loginKey)
    }

    private fun afterResolve() {
        emitFirstOpenOnce()
    }

    /** Queues `first_open` once per installation; a consent drop leaves it for a later chance. */
    private fun emitFirstOpenOnce() {
        if (state.isFirstOpenTracked) return
        if (enqueueGated(DleEvent.firstOpen())) state.isFirstOpenTracked = true
    }

    /** Applies the consent gate and buffers the event. Returns whether it was buffered. */
    private fun enqueueGated(event: DleEvent): Boolean {
        if (config.requireAnalyticsConsentForEvents && event.type.requiresAnalyticsConsent && !consent.analytics) {
            log.debug("${event.type.wireName} dropped: no analytics consent")
            return false
        }
        queue.enqueue(event)
        return true
    }

    private fun onMain(block: () -> Unit) {
        val handler = mainHandler
        if (handler == null || Looper.myLooper() == handler.looper) {
            block()
        } else {
            handler.post { block() }
        }
    }

    /** Observes activities so App Link opens are reported without any call from the host. */
    private inner class OpenReporter : Application.ActivityLifecycleCallbacks {
        override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) {
            if (savedInstanceState == null) {
                handleIntent(activity.intent)
            } else {
                appLinkHandler.markReported(activity.intent)
            }
        }

        override fun onActivityResumed(activity: Activity) {
            handleIntent(activity.intent)
        }

        override fun onActivityStarted(activity: Activity) = Unit

        override fun onActivityPaused(activity: Activity) = Unit

        override fun onActivityStopped(activity: Activity) = Unit

        override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit

        override fun onActivityDestroyed(activity: Activity) = Unit
    }

    internal companion object {
        private const val MILLIS_PER_MINUTE = 60_000
        private const val QUEUE_DIRECTORY = "sk.magors.dle"
        private const val INSTALL_MARKER = "sk.magors.dle.installed"

        /** Wires the client together. */
        fun create(application: Context, config: DleConfig): DleClient {
            val log = SdkLog(config.logLevel, config.logger ?: AndroidDleLogger)
            val clock = Clock.SYSTEM
            val scope = CoroutineScope(
                SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler { _, e -> log.error("unhandled SDK failure", e) },
            )
            val installIdStore = InstallIdStore.open(application, log)
            val state = StateStore(application.getSharedPreferences(StateStore.FILE, Context.MODE_PRIVATE))
            if (claimInstallMarker(application, log)) {
                // First process of this installation on this device. Auto Backup may have restored
                // another installation's id, cached answer and consent; none of them belong here.
                installIdStore.clear()
                state.clear()
            }
            val appVersion = config.appVersion ?: readAppVersion(application)
            val api = DleApi(config, signalsProvider = { collectSignals(application, clock) }, log = log)
            val installId = installIdStore.getOrCreate()
            val queue = EventQueue(
                // Never backed up, so a restore cannot replay another installation's events.
                directory = File(application.noBackupFilesDir, QUEUE_DIRECTORY),
                maxSize = config.maxQueueSize,
                flushDelayMillis = config.flushDelayMillis,
                sender = { batch ->
                    api.sendEvents(
                        EventBatch(installId = installId, platform = PLATFORM_ANDROID, appVersion = appVersion, events = batch),
                    )
                },
                scope = scope,
                log = log,
                clock = clock,
            )
            val referrerReader = InstallReferrerReader(application, state, config.referrerTimeoutMillis, log)
            val client = DleClient(
                application, config, appVersion, log, installIdStore, state, api, queue, referrerReader, scope, clock,
            )
            if (config.autoReportOpens) {
                val app = application as? Application
                if (app != null) {
                    app.registerActivityLifecycleCallbacks(client.OpenReporter())
                } else {
                    log.warn("autoReportOpens needs an Application context; call handleIntent yourself")
                }
            }
            if (queue.size > 0) queue.flush()
            log.info("initialised v${Dle.VERSION} against ${config.endpoint}")
            return client
        }

        /**
         * Coarse device signals for `DeviceSignalsDto`: the UI language, the screen size in
         * pixels, the UTC offset in minutes and the model name. Each is shared by millions of
         * devices; nothing is hashed or combined. Invoked by [DleApi] only when the operator and
         * the user both opted in (spec §0.2, §A.6).
         */
        fun collectSignals(application: Context, clock: Clock): SignalsWire {
            val metrics = try {
                application.resources.displayMetrics
            } catch (_: RuntimeException) {
                null
            }
            val offsetMinutes = TimeZone.getDefault().getOffset(clock.nowMillis()) / MILLIS_PER_MINUTE
            return SignalsWire(
                language = Locale.getDefault().toLanguageTag().takeIf { it.isNotBlank() && it != "und" },
                screen = metrics?.let { "${it.widthPixels}x${it.heightPixels}" },
                tzOffset = offsetMinutes,
                deviceModel = Build.MODEL?.takeIf { it.isNotBlank() },
            )
        }

        /**
         * Creates the install marker and returns `true` when it was missing. The marker lives in
         * `noBackupFilesDir`, which Auto Backup and device transfer never copy, so it is missing
         * after a fresh install and after a restore, while the SharedPreferences holding the
         * install id and the state may have been restored. A marker that cannot be written
         * returns `false`, so a broken disk does not rotate the install id on every launch.
         */
        private fun claimInstallMarker(application: Context, log: SdkLog): Boolean = try {
            val marker = File(application.noBackupFilesDir, INSTALL_MARKER)
            !marker.exists() && marker.createNewFile()
        } catch (e: IOException) {
            log.warn("install marker could not be written", e)
            false
        } catch (e: SecurityException) {
            log.warn("install marker could not be written", e)
            false
        }

        private fun readAppVersion(context: Context): String? = try {
            val pm = context.packageManager
            val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                pm.getPackageInfo(context.packageName, PackageManager.PackageInfoFlags.of(0))
            } else {
                @Suppress("DEPRECATION")
                pm.getPackageInfo(context.packageName, 0)
            }
            info.versionName?.takeIf { it.isNotBlank() }
        } catch (_: PackageManager.NameNotFoundException) {
            null
        } catch (_: RuntimeException) {
            null
        }
    }
}
