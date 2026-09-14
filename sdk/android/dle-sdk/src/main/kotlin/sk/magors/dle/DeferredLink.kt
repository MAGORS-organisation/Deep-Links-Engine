package sk.magors.dle

/**
 * The answer to `POST /v1/resolve`: which click, if any, led to this installation. Mirrors
 * `ResolveResponseDto` member for member (spec §B.7.2).
 *
 * [matchType] and [confidence] are non-nullable on purpose. Every attribution carries both, and a
 * response without them is refused by the SDK as malformed rather than defaulted (FR-186,
 * ADR-008). A [MatchType.PROBABILISTIC] result with [confidence] `0.6` is a hint; treat it as a
 * suggestion in the UI, never as fact. Use [isDeterministic] before acting irreversibly.
 *
 * @property matched whether any strategy matched. When `false` the application continues with its
 * normal onboarding (TC-142).
 * @property matchType the strategy that produced the match.
 * @property confidence confidence from 0.0 to 1.0. Exactly 1.0 for deterministic strategies.
 * @property clickId identifier of the matched click, when there is one.
 * @property link the matched link, when there is one.
 * @property params parameters handed to the application: the UTM set of the link plus custom
 * data. Keys keep their original spelling.
 * @property expiresIn seconds this result stays valid. Zero means it is final and the SDK never
 * asks again for this installation (TC-143).
 */
public data class DeferredLink(
    public val matched: Boolean,
    public val matchType: MatchType,
    public val confidence: Double,
    public val clickId: String? = null,
    public val link: ResolvedLink? = null,
    public val params: Map<String, String> = emptyMap(),
    public val expiresIn: Int = 0,
) {
    /**
     * `true` only for a match the engine established deterministically with full confidence.
     * A probabilistic match never qualifies, even if a server bug reported confidence 1.0.
     */
    public val isDeterministic: Boolean
        get() = matched && matchType.isDeterministic && confidence >= 1.0

    /** `true` when this result is final and must not be re-requested. */
    public val isFinal: Boolean
        get() = expiresIn <= 0

    public companion object {
        /** The result for an installation nothing matched. */
        @JvmStatic
        public fun none(): DeferredLink = DeferredLink(matched = false, matchType = MatchType.NONE, confidence = 0.0)
    }
}

/**
 * The link an installation was attributed to, reduced to what the client needs to navigate.
 * Mirrors `ResolveLinkDto`.
 *
 * @property id link identifier. A string, because the value is a 64 bit Snowflake.
 * @property deeplinkPath path the application should open, for example `/promo/autumn`. Untrusted
 * input like any deep link: validate it with [DeepLinkAllowlist] before navigating (spec §E.7).
 * @property campaign campaign name carried by the link.
 * @property title human readable link title.
 */
public data class ResolvedLink(
    public val id: String,
    public val deeplinkPath: String? = null,
    public val campaign: String? = null,
    public val title: String? = null,
)
