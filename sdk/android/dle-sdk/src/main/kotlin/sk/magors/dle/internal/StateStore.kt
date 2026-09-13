package sk.magors.dle.internal

import android.content.SharedPreferences
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import sk.magors.dle.DeferredLink
import sk.magors.dle.DleConsent
import sk.magors.dle.ResolveOptions

/** How the one and only Install Referrer read ended (spec §A.2.4). */
internal enum class ReferrerStatus {
    /** The Play Store answered; [ReferrerOutcome.referrer] holds the raw string. */
    OK,

    /** `FEATURE_NOT_SUPPORTED`: the installed Play Store is too old. Terminal. */
    NOT_SUPPORTED,

    /** `DEVELOPER_ERROR`: the connection was misused. Terminal; please report it. */
    DEVELOPER_ERROR,

    /** `PERMISSION_ERROR`: the application may not talk to the Play Store. Terminal. */
    PERMISSION_ERROR,

    /** `SERVICE_UNAVAILABLE` or `SERVICE_DISCONNECTED` kept happening until the deadline. */
    UNAVAILABLE,

    /** The Play Store did not answer inside [sk.magors.dle.DleConfig.referrerTimeoutMillis]. */
    TIMEOUT,

    /** The client threw, for example a `RemoteException` while reading the details. */
    ERROR,
    ;

    /** `true` when the outcome will not change on this device, so a retry is pointless. */
    val isTerminal: Boolean
        get() = this == OK || this == NOT_SUPPORTED || this == DEVELOPER_ERROR || this == PERMISSION_ERROR
}

/**
 * The persisted outcome of the Install Referrer read.
 *
 * @property status how the read ended.
 * @property referrer the raw referrer string, only for [ReferrerStatus.OK]. It is sent to the
 * server verbatim (clipped) and parsed locally with [InstallReferrerParser]; the SDK never logs it.
 */
@Serializable
internal data class ReferrerOutcome(
    @SerialName("status") val status: ReferrerStatus,
    @SerialName("referrer") val referrer: String? = null,
) {
    /** The click identifier the engine wrote into the store URL, or `null` for an organic install. */
    val clickId: String?
        get() = if (status == ReferrerStatus.OK) InstallReferrerParser.clickId(referrer) else null
}

/**
 * A resolve answer kept for the life of the installation (TC-143).
 *
 * @property link the answer.
 * @property resolvedAtMillis when it was received, to age a non final answer.
 */
internal data class CachedResolve(
    val link: DeferredLink,
    val resolvedAtMillis: Long,
) {
    /**
     * Whether a resolve call must go to the server despite this cached answer. Normally it must
     * not (TC-143). The two exceptions: the answer is not final and has aged past
     * [DeferredLink.expiresIn], or the caller brings new deterministic evidence
     * ([ResolveOptions.hasExplicitEvidence]) while the stored answer is not deterministic itself.
     *
     * @param options the evidence of the call.
     * @param nowMillis the current time.
     */
    fun needsRequest(options: ResolveOptions, nowMillis: Long): Boolean {
        if (options.hasExplicitEvidence && !link.isDeterministic) return true
        if (!link.isFinal && nowMillis >= resolvedAtMillis + link.expiresIn * 1000L) return true
        return false
    }
}

/**
 * Everything the SDK remembers between launches other than the install id and the event queue:
 * whether the resolve happened and what it answered, whether the referrer was read and what it
 * said, whether `first_open` went out, and the recorded consent.
 *
 * `SharedPreferences` backed with JSON values, so a field added later is ignored by an older SDK
 * and defaulted by a newer one. Every write is committed synchronously: a resolve that ran but
 * was not remembered would run again on the next launch, which is exactly what TC-143 forbids.
 *
 * @param prefs the backing store, a private file of the SDK.
 * @param json the serializer.
 */
internal class StateStore(
    private val prefs: SharedPreferences,
    private val json: Json = DleJson,
) {
    // -------------------------------------------------------------------------------------------
    // Resolve
    // -------------------------------------------------------------------------------------------

    /** Whether a resolve has ever completed for this installation. */
    val isResolveDone: Boolean
        get() = prefs.getBoolean(KEY_RESOLVE_DONE, false)

    /** The cached answer, or `null` when no resolve has completed or the cache is unreadable. */
    fun cachedResolve(): CachedResolve? {
        if (!isResolveDone) return null
        val text = prefs.getString(KEY_RESOLVE_LINK, null) ?: return null
        val at = prefs.getLong(KEY_RESOLVE_AT, 0L)
        val link = try {
            json.decodeFromString(ResolveResponse.serializer(), text).toDeferredLink()
        } catch (_: SerializationException) {
            return null
        } catch (_: IllegalArgumentException) {
            return null
        } catch (_: sk.magors.dle.DleException) {
            return null
        }
        return CachedResolve(link, at)
    }

    /** Remembers a completed resolve. */
    fun saveResolve(link: DeferredLink, atMillis: Long) {
        prefs.edit()
            .putBoolean(KEY_RESOLVE_DONE, true)
            .putString(KEY_RESOLVE_LINK, json.encodeToString(ResolveResponse.serializer(), link.toWire()))
            .putLong(KEY_RESOLVE_AT, atMillis)
            .commit()
    }

    /**
     * Forgets the cached answer but not the fact that a resolve happened, so the next resolve
     * asks the engine again while `first_open` stays emitted once. Used when a non-final answer
     * became stale for a reason other than time: attribution consent was recorded after it.
     */
    fun clearResolve() {
        prefs.edit()
            .remove(KEY_RESOLVE_LINK)
            .remove(KEY_RESOLVE_AT)
            .commit()
    }

    // -------------------------------------------------------------------------------------------
    // Install Referrer
    // -------------------------------------------------------------------------------------------

    /** The outcome of the one Install Referrer read, or `null` when it has not been attempted. */
    fun referrerOutcome(): ReferrerOutcome? {
        val text = prefs.getString(KEY_REFERRER, null) ?: return null
        return try {
            json.decodeFromString(ReferrerOutcome.serializer(), text)
        } catch (_: SerializationException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    /** Whether the Install Referrer read has been attempted. */
    val isReferrerAttempted: Boolean
        get() = prefs.contains(KEY_REFERRER)

    /** Remembers the outcome of the Install Referrer read. */
    fun saveReferrerOutcome(outcome: ReferrerOutcome) {
        prefs.edit().putString(KEY_REFERRER, json.encodeToString(ReferrerOutcome.serializer(), outcome)).commit()
    }

    // -------------------------------------------------------------------------------------------
    // first_open
    // -------------------------------------------------------------------------------------------

    /** Whether the `first_open` event has been queued once. */
    var isFirstOpenTracked: Boolean
        get() = prefs.getBoolean(KEY_FIRST_OPEN, false)
        set(value) {
            prefs.edit().putBoolean(KEY_FIRST_OPEN, value).commit()
        }

    // -------------------------------------------------------------------------------------------
    // Consent
    // -------------------------------------------------------------------------------------------

    /** The recorded consent decision, or `null` when nobody recorded one. */
    fun consent(): DleConsent? {
        if (!prefs.contains(KEY_CONSENT_TS)) return null
        return DleConsent(
            analytics = prefs.getBoolean(KEY_CONSENT_ANALYTICS, false),
            attribution = prefs.getBoolean(KEY_CONSENT_ATTRIBUTION, false),
            timestampMillis = prefs.getLong(KEY_CONSENT_TS, 0L),
        )
    }

    /** Remembers a consent decision. It must carry a timestamp; the caller stamps one. */
    fun saveConsent(consent: DleConsent) {
        val ts = consent.timestampMillis ?: return
        prefs.edit()
            .putBoolean(KEY_CONSENT_ANALYTICS, consent.analytics)
            .putBoolean(KEY_CONSENT_ATTRIBUTION, consent.attribution)
            .putLong(KEY_CONSENT_TS, ts)
            .commit()
    }

    /** Forgets everything. Used by tests and by a host that erases the installation. */
    fun clear() {
        prefs.edit().clear().commit()
    }

    internal companion object {
        const val FILE: String = "sk.magors.dle.state"
        const val KEY_RESOLVE_DONE: String = "resolve_done"
        const val KEY_RESOLVE_LINK: String = "resolve_link"
        const val KEY_RESOLVE_AT: String = "resolve_at"
        const val KEY_REFERRER: String = "referrer_outcome"
        const val KEY_FIRST_OPEN: String = "first_open_tracked"
        const val KEY_CONSENT_ANALYTICS: String = "consent_analytics"
        const val KEY_CONSENT_ATTRIBUTION: String = "consent_attribution"
        const val KEY_CONSENT_TS: String = "consent_ts"
    }
}
