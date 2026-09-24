package sk.magors.dle.internal

import android.content.Context
import android.os.RemoteException
import com.android.installreferrer.api.InstallReferrerClient
import com.android.installreferrer.api.InstallReferrerClient.InstallReferrerResponse
import com.android.installreferrer.api.InstallReferrerStateListener
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.resume

/**
 * Reads the Google Play Install Referrer exactly once per installation (S1, FR-182, spec §A.2.4).
 *
 * The outcome, whatever it is, is persisted through [StateStore] before it is returned, so no
 * later launch connects to the Play Store again. Play only answers the referrer query for a
 * limited time after installation anyway, and a second read can never say more than the first.
 *
 * Response handling follows the library's documentation: `OK` yields the raw string;
 * `FEATURE_NOT_SUPPORTED`, `DEVELOPER_ERROR` and `PERMISSION_ERROR` are terminal;
 * `SERVICE_UNAVAILABLE` and `SERVICE_DISCONNECTED` are retried with exponential backoff until
 * [timeoutMillis] has elapsed. The connection is closed after every attempt.
 *
 * @param context any context; the application context is used.
 * @param state where the outcome is persisted.
 * @param timeoutMillis [sk.magors.dle.DleConfig.referrerTimeoutMillis]. Zero skips the read.
 * @param log the SDK log. The referrer value itself is never logged.
 */
internal class InstallReferrerReader(
    context: Context,
    private val state: StateStore,
    private val timeoutMillis: Long,
    private val log: SdkLog,
) {
    private val context: Context = context.applicationContext

    /**
     * Returns the persisted outcome, performing the read on the first call only.
     */
    suspend fun readOnce(): ReferrerOutcome {
        state.referrerOutcome()?.let { return it }
        val outcome = if (timeoutMillis <= 0L) {
            ReferrerOutcome(ReferrerStatus.TIMEOUT)
        } else {
            sawTransientFailure = false
            withTimeoutOrNull(timeoutMillis) { readWithRetries() }
                ?: ReferrerOutcome(if (sawTransientFailure) ReferrerStatus.UNAVAILABLE else ReferrerStatus.TIMEOUT)
        }
        state.saveReferrerOutcome(outcome)
        when (outcome.status) {
            ReferrerStatus.OK -> log.info(
                "install referrer read: " + if (outcome.clickId == null) "organic" else "click id present",
            )
            else -> log.info("install referrer unavailable: ${outcome.status}")
        }
        return outcome
    }

    /** Whether Play answered `SERVICE_UNAVAILABLE` or disconnected during the current read. */
    @Volatile
    private var sawTransientFailure = false

    private suspend fun readWithRetries(): ReferrerOutcome {
        var backoff = INITIAL_BACKOFF_MILLIS
        while (true) {
            when (val attempt = connectOnce()) {
                is Attempt.Done -> return attempt.outcome
                Attempt.Transient -> {
                    sawTransientFailure = true
                    log.debug("install referrer service unavailable, retrying in $backoff ms")
                    delay(backoff)
                    backoff = (backoff * 2).coerceAtMost(MAX_BACKOFF_MILLIS)
                }
            }
        }
    }

    private sealed class Attempt {
        class Done(val outcome: ReferrerOutcome) : Attempt()

        object Transient : Attempt()
    }

    /** One connect, read and disconnect cycle. Never throws. */
    private suspend fun connectOnce(): Attempt {
        val client = try {
            InstallReferrerClient.newBuilder(context).build()
        } catch (e: RuntimeException) {
            log.warn("install referrer client could not be created", e)
            return Attempt.Done(ReferrerOutcome(ReferrerStatus.ERROR))
        }
        try {
            return suspendCancellableCoroutine { continuation ->
                continuation.invokeOnCancellation { endQuietly(client) }
                val listener = object : InstallReferrerStateListener {
                    override fun onInstallReferrerSetupFinished(responseCode: Int) {
                        if (!continuation.isActive) return
                        continuation.resume(onSetupFinished(client, responseCode))
                    }

                    override fun onInstallReferrerServiceDisconnected() {
                        // Fires after a successful setup too, when Play goes away; only a
                        // disconnect before the answer is a failed attempt.
                        if (!continuation.isActive) return
                        continuation.resume(Attempt.Transient)
                    }
                }
                try {
                    client.startConnection(listener)
                } catch (e: RuntimeException) {
                    // Some devices throw SecurityException or IllegalStateException here.
                    if (continuation.isActive) {
                        log.warn("install referrer connection failed", e)
                        continuation.resume(Attempt.Done(ReferrerOutcome(ReferrerStatus.ERROR)))
                    }
                }
            }
        } finally {
            endQuietly(client)
        }
    }

    private fun onSetupFinished(client: InstallReferrerClient, responseCode: Int): Attempt = when (responseCode) {
        InstallReferrerResponse.OK -> Attempt.Done(readDetails(client))
        InstallReferrerResponse.FEATURE_NOT_SUPPORTED -> Attempt.Done(ReferrerOutcome(ReferrerStatus.NOT_SUPPORTED))
        InstallReferrerResponse.DEVELOPER_ERROR -> Attempt.Done(ReferrerOutcome(ReferrerStatus.DEVELOPER_ERROR))
        InstallReferrerResponse.PERMISSION_ERROR -> Attempt.Done(ReferrerOutcome(ReferrerStatus.PERMISSION_ERROR))
        InstallReferrerResponse.SERVICE_UNAVAILABLE, InstallReferrerResponse.SERVICE_DISCONNECTED -> Attempt.Transient
        else -> Attempt.Done(ReferrerOutcome(ReferrerStatus.ERROR))
    }

    private fun readDetails(client: InstallReferrerClient): ReferrerOutcome = try {
        val raw = client.installReferrer.installReferrer
        ReferrerOutcome(ReferrerStatus.OK, InstallReferrerParser.clip(raw))
    } catch (e: RemoteException) {
        log.warn("install referrer details unreadable", e)
        ReferrerOutcome(ReferrerStatus.ERROR)
    } catch (e: RuntimeException) {
        log.warn("install referrer details unreadable", e)
        ReferrerOutcome(ReferrerStatus.ERROR)
    }

    private fun endQuietly(client: InstallReferrerClient) {
        try {
            client.endConnection()
        } catch (_: RuntimeException) {
            // Already closed, or never opened.
        }
    }

    private companion object {
        const val INITIAL_BACKOFF_MILLIS = 500L
        const val MAX_BACKOFF_MILLIS = 4_000L
    }
}
