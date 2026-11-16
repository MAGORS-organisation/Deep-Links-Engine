package net.kyberware.dle

/**
 * How an attribution was established. Mirrors the `match_type` member of `ResolveResponseDto`
 * (`Dle.Domain.Contracts.SdkContracts`).
 *
 * The product rule this enum exists to enforce: **a probabilistic match is never presented as a
 * certainty**. Always read [DeferredLink.confidence] alongside this value and
 * branch on [isDeterministic] before doing anything irreversible for the user, such as granting a
 * referral bonus.
 */
public enum class MatchType(
    /** The exact string the server sends. */
    public val wireName: String,
) {
    /** No attribution could be established. The application runs its normal onboarding. */
    NONE("none"),

    /** The Google Play Install Referrer carried the click identifier (S1). */
    INSTALL_REFERRER("install_referrer"),

    /** The user signed in and the account was reconciled with a pending click (S2). */
    LOGIN("login"),

    /** The user typed the short code shown on the interstitial page (S3). */
    CLAIM_CODE("claim_code"),

    /**
     * A statistical match inside the configured window (S4). Opt-in, consent gated, and always
     * accompanied by a confidence below 1.0. A hint, never a fact.
     */
    PROBABILISTIC("probabilistic"),

    /**
     * The application was already installed and the operating system opened it straight from a
     * verified App Link. Produced from the `link_open` event the SDK reports,
     * never inferred by the server.
     */
    DIRECT_OPEN("direct_open"),
    ;

    /**
     * `true` for match types that identify the click exactly, with no statistical inference.
     * Only these may drive irreversible or user visible personalisation.
     */
    public val isDeterministic: Boolean
        get() = when (this) {
            INSTALL_REFERRER, LOGIN, CLAIM_CODE, DIRECT_OPEN -> true
            NONE, PROBABILISTIC -> false
        }

    public companion object {
        /**
         * Looks a wire value up.
         *
         * @param value the `match_type` string as received.
         * @return the matching type, or `null` for anything the SDK does not know. The response
         * parser reads an unknown value as [NONE] — "not attributed", never "attributed somehow" —
         * because an unknown strategy presented as an attribution is exactly the failure mode this
         * product forbids, and refusing the whole answer would break every app on a newer engine.
         */
        @JvmStatic
        public fun fromWireName(value: String): MatchType? = entries.firstOrNull { it.wireName == value }
    }
}
