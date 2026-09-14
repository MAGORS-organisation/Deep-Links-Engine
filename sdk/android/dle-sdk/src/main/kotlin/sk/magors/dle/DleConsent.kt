package sk.magors.dle

/**
 * The user's consent state, as the host application collected it.
 *
 * The engine is consent first: consent is an **input to the decision**, not a filter applied
 * afterwards (spec §E.6.2). For this SDK concretely:
 *
 * - `attribution == false` means the `signals` object is **omitted** from `POST /v1/resolve`
 *   entirely (not sent empty), the Install Referrer is not transmitted, and no click can be linked
 *   to this installation (TC-145, TC-146).
 * - `analytics == false` means behavioural events (`first_open`, `session`, `conversion`,
 *   `custom`) are dropped at [DleClient.track] time, never buffered. `link_open` is exempt: the
 *   engine treats a direct open as first party measurement of the link itself and FR-223 requires
 *   it to be reported. Set [DleConfig.Builder.requireAnalyticsConsentForEvents] to `false` to lift
 *   the gate when your legal basis does not need it.
 *
 * The SDK never infers consent. Until [DleClient.setConsent] is called, the value from
 * [DleConfig.consent] applies, and its default is [denied]. A recorded decision is persisted and
 * survives restarts; it carries a timestamp so it stays auditable, and it is sent to the server as
 * the `consent` member of the resolve request.
 *
 * @property analytics the user agreed to measurement of in-app behaviour.
 * @property attribution the user agreed to linking this installation to a prior click.
 * @property timestampMillis when the decision was recorded, in epoch milliseconds. `null` for a
 * default that nobody recorded; such a value is not sent to the server at all, because
 * `{false, false}` would be indistinguishable from an explicit refusal.
 */
public data class DleConsent(
    public val analytics: Boolean,
    public val attribution: Boolean,
    public val timestampMillis: Long? = null,
) {
    /** Whether a person actually recorded this decision (as opposed to the SDK default). */
    public val isRecorded: Boolean
        get() = timestampMillis != null

    /**
     * Whether coarse device signals may be collected and transmitted at all. Signals are
     * additionally gated on [DleConfig.probabilisticSignals]: consent alone does not switch
     * probabilistic matching on (spec §0.2, §A.6).
     */
    public val allowsDeviceSignals: Boolean
        get() = attribution

    public companion object {
        /** Nothing is allowed. The SDK default. */
        @JvmStatic
        public fun denied(): DleConsent = DleConsent(analytics = false, attribution = false)

        /**
         * Everything is allowed.
         *
         * @param timestampMillis when the user decided; `null` lets [DleClient.setConsent] stamp it.
         */
        @JvmStatic
        @JvmOverloads
        public fun granted(timestampMillis: Long? = null): DleConsent =
            DleConsent(analytics = true, attribution = true, timestampMillis = timestampMillis)

        /**
         * Behavioural measurement only: no click-to-install linking and no device signals.
         *
         * @param timestampMillis when the user decided; `null` lets [DleClient.setConsent] stamp it.
         */
        @JvmStatic
        @JvmOverloads
        public fun analyticsOnly(timestampMillis: Long? = null): DleConsent =
            DleConsent(analytics = true, attribution = false, timestampMillis = timestampMillis)
    }
}
