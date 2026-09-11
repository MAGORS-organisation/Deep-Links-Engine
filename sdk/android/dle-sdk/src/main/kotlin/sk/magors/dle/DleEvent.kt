package sk.magors.dle

/**
 * Event kinds accepted by `POST /v1/events` (`SdkEventNames`, spec §B.7.2).
 *
 * @property wireName the exact string sent as `type`.
 * @property requiresAnalyticsConsent whether [DleClient.track] drops the event without
 * [DleConsent.analytics]. `link_open` is exempt: the engine records a direct open as first party
 * measurement of the link itself, no identifier crosses a context, and FR-223 requires the report.
 */
public enum class DleEventType(
    public val wireName: String,
    public val requiresAnalyticsConsent: Boolean,
) {
    /**
     * The application was opened through one of our links (spec §B.6.4, FR-223). The most
     * important event there is: a verified App Link opens the application without any request
     * reaching the engine, so this report is the only evidence the click ever happened.
     */
    LINK_OPEN("link_open", requiresAnalyticsConsent = false),

    /** First launch after an installation. Sent automatically once, after the first resolve. */
    FIRST_OPEN("first_open", requiresAnalyticsConsent = true),

    /** A foreground session started. */
    SESSION("session", requiresAnalyticsConsent = true),

    /** A conversion, optionally carrying a monetary value. */
    CONVERSION("conversion", requiresAnalyticsConsent = true),

    /** Anything the host application defines for itself. */
    CUSTOM("custom", requiresAnalyticsConsent = true),
    ;

    public companion object {
        /**
         * Looks a wire value up.
         *
         * @param value the `type` string.
         * @return the type, or `null` when unknown.
         */
        @JvmStatic
        public fun fromWireName(value: String): DleEventType? = entries.firstOrNull { it.wireName == value }
    }
}

/**
 * A single event queued for `POST /v1/events`. Mirrors `EventDto` (spec §B.7.2). Build instances
 * through the factories in the companion object; they enforce what the server would otherwise
 * reject.
 *
 * Keep [properties] free of personal data: the engine stores them as sent, and the deletion
 * endpoint keyed on `install_id` is the only remedy afterwards (spec §E.6.3).
 *
 * @property type the event kind.
 * @property name name of a conversion or custom event, for example `purchase`.
 * @property url URL that opened the application, for a [DleEventType.LINK_OPEN] event.
 * @property value monetary value of a conversion.
 * @property currency ISO 4217 currency code accompanying [value].
 * @property timestampMillis when the event happened on the device, in epoch milliseconds.
 * `null` is stamped at enqueue time.
 * @property properties additional flat string properties.
 */
public data class DleEvent(
    public val type: DleEventType,
    public val name: String? = null,
    public val url: String? = null,
    public val value: Double? = null,
    public val currency: String? = null,
    public val timestampMillis: Long? = null,
    public val properties: Map<String, String>? = null,
) {
    init {
        require(type != DleEventType.CONVERSION && type != DleEventType.CUSTOM || !name.isNullOrBlank()) {
            "a ${type.wireName} event needs a name"
        }
        require(type != DleEventType.LINK_OPEN || !url.isNullOrBlank()) { "a link_open event needs a url" }
        require(value == null || value.isFinite()) { "value must be a finite number" }
        require(currency == null || currency.length == CURRENCY_LENGTH) { "currency must be an ISO 4217 code" }
        require(properties == null || properties.size <= MAX_PROPERTIES) { "at most $MAX_PROPERTIES properties" }
    }

    override fun toString(): String = "DleEvent(type=${type.wireName}, name=$name)"

    public companion object {
        /** Length of an ISO 4217 currency code. */
        public const val CURRENCY_LENGTH: Int = 3

        /** Upper bound on [properties] entries, so a batch can never approach the server's body limit. */
        public const val MAX_PROPERTIES: Int = 32

        /**
         * The application was opened through a link. [DleClient.handleIntent] creates this for
         * you; call it directly only when you route intents yourself.
         *
         * @param url the URL the application was opened with. Send the scheme, host and path;
         * the query string is not needed and may carry personal data.
         * @param timestampMillis when it happened; `null` means now.
         */
        @JvmStatic
        @JvmOverloads
        public fun linkOpen(url: String, timestampMillis: Long? = null): DleEvent =
            DleEvent(type = DleEventType.LINK_OPEN, url = url, timestampMillis = timestampMillis)

        /** First launch after installation. */
        @JvmStatic
        public fun firstOpen(): DleEvent = DleEvent(type = DleEventType.FIRST_OPEN)

        /** A foreground session started. */
        @JvmStatic
        public fun session(): DleEvent = DleEvent(type = DleEventType.SESSION)

        /**
         * A conversion.
         *
         * @param name what converted, for example `purchase`.
         * @param value monetary value, when there is one.
         * @param currency ISO 4217 code such as `EUR`, upper cased for you.
         * @param properties additional flat string properties.
         */
        @JvmStatic
        @JvmOverloads
        public fun conversion(
            name: String,
            value: Double? = null,
            currency: String? = null,
            properties: Map<String, String>? = null,
        ): DleEvent = DleEvent(
            type = DleEventType.CONVERSION,
            name = name.trim(),
            value = value,
            currency = currency?.trim()?.uppercase(),
            properties = properties,
        )

        /**
         * An application defined event.
         *
         * @param name the event name.
         * @param properties additional flat string properties.
         */
        @JvmStatic
        @JvmOverloads
        public fun custom(name: String, properties: Map<String, String>? = null): DleEvent =
            DleEvent(type = DleEventType.CUSTOM, name = name.trim(), properties = properties)
    }
}
