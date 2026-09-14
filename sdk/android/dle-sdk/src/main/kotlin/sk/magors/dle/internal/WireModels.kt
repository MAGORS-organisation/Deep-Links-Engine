package sk.magors.dle.internal

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import sk.magors.dle.DeferredLink
import sk.magors.dle.DleConsent
import sk.magors.dle.DleEvent
import sk.magors.dle.DleException
import sk.magors.dle.MatchType
import sk.magors.dle.ProblemDocument
import sk.magors.dle.ResolvedLink

/**
 * The wire shapes of `Dle.Domain.Contracts.SdkContracts` (spec §B.7.2), one class per DTO, every
 * member in the DTO's declaration order and named with the exact snake_case string the server
 * uses. Bodies produced from these classes are byte comparable with the literals in
 * `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`; `WireModelsTest` asserts that.
 *
 * Absent optional members are omitted, never written as `null`: the server serialises with
 * `WhenWritingNull` and the SDK mirrors it so a `signals` object that must not be sent is
 * genuinely absent (TC-145, TC-146), not present and empty.
 */
internal val DleJson: Json = Json {
    // A new server may add members; an SDK already in the field must keep working.
    ignoreUnknownKeys = true
    // Absent nullable members decode as null and null members are not encoded at all.
    explicitNulls = false
    // Members equal to their default are omitted; there are no defaults on required members.
    encodeDefaults = false
}

/** Client platform string sent as `platform`. */
internal const val PLATFORM_ANDROID: String = "android"

/** `EventBatchDto.MaxEventsPerBatch`. */
internal const val MAX_EVENTS_PER_BATCH: Int = 100

/** `DeviceSignalsDto`. Coarse by design; nothing here is a stable device identifier. */
@Serializable
internal data class SignalsWire(
    @SerialName("language") val language: String? = null,
    @SerialName("screen") val screen: String? = null,
    @SerialName("tz_offset") val tzOffset: Int? = null,
    @SerialName("device_model") val deviceModel: String? = null,
)

/** `ConsentDto`. */
@Serializable
internal data class ConsentWire(
    @SerialName("analytics") val analytics: Boolean,
    @SerialName("attribution") val attribution: Boolean,
    @SerialName("ts") val ts: String? = null,
)

/** `ResolveRequestDto`. */
@Serializable
internal data class ResolveRequest(
    @SerialName("install_id") val installId: String,
    @SerialName("platform") val platform: String,
    @SerialName("app_version") val appVersion: String? = null,
    @SerialName("os_version") val osVersion: String? = null,
    @SerialName("referrer") val referrer: String? = null,
    @SerialName("claim_code") val claimCode: String? = null,
    @SerialName("login_key") val loginKey: String? = null,
    @SerialName("signals") val signals: SignalsWire? = null,
    @SerialName("consent") val consent: ConsentWire? = null,
)

/** `ResolveLinkDto`. */
@Serializable
internal data class LinkWire(
    @SerialName("id") val id: String,
    @SerialName("deeplink_path") val deeplinkPath: String? = null,
    @SerialName("campaign") val campaign: String? = null,
    @SerialName("title") val title: String? = null,
)

/** `ResolveResponseDto`. Also the persisted form of a cached [DeferredLink]. */
@Serializable
internal data class ResolveResponse(
    @SerialName("matched") val matched: Boolean,
    @SerialName("match_type") val matchType: String,
    @SerialName("confidence") val confidence: Double,
    @SerialName("click_id") val clickId: String? = null,
    @SerialName("link") val link: LinkWire? = null,
    @SerialName("params") val params: Map<String, String> = emptyMap(),
    @SerialName("expires_in") val expiresIn: Int = 0,
)

/** `EventDto`. */
@Serializable
internal data class EventWire(
    @SerialName("type") val type: String,
    @SerialName("name") val name: String? = null,
    @SerialName("url") val url: String? = null,
    @SerialName("value") val value: Double? = null,
    @SerialName("currency") val currency: String? = null,
    @SerialName("ts") val ts: String? = null,
    @SerialName("properties") val properties: Map<String, String>? = null,
)

/** `EventBatchDto`. */
@Serializable
internal data class EventBatch(
    @SerialName("install_id") val installId: String,
    @SerialName("platform") val platform: String? = null,
    @SerialName("app_version") val appVersion: String? = null,
    @SerialName("events") val events: List<EventWire>,
)

/** `EventBatchAcceptedDto`. */
@Serializable
internal data class EventBatchAccepted(
    @SerialName("accepted") val accepted: Int = 0,
    @SerialName("rejected") val rejected: Int = 0,
)

/**
 * An RFC 9457 problem document with the two extension members the attribution endpoints add
 * (`AttributionProblem.ReasonExtension`, `CanReissueExtension`). Everything else is ignored.
 */
@Serializable
internal data class ProblemWire(
    @SerialName("type") val type: String? = null,
    @SerialName("title") val title: String? = null,
    @SerialName("status") val status: Int? = null,
    @SerialName("detail") val detail: String? = null,
    @SerialName("instance") val instance: String? = null,
    @SerialName("reason") val reason: String? = null,
    @SerialName("can_reissue") val canReissue: Boolean? = null,
) {
    /** The public form. */
    fun toProblemDocument(): ProblemDocument = ProblemDocument(
        type = type,
        title = title,
        status = status,
        detail = detail,
        instance = instance,
        reason = reason,
        canReissue = canReissue,
    )
}

// ---------------------------------------------------------------------------------------------
// Mapping between the public types and the wire
// ---------------------------------------------------------------------------------------------

/**
 * Converts the server's answer into the public type.
 *
 * @throws DleException.Malformed when `match_type` is not one the SDK knows or `confidence` is
 * not a finite number. An unknown strategy presented as an attribution is exactly the failure
 * mode this product forbids (FR-186, ADR-008), so the body is refused rather than guessed at.
 */
internal fun ResolveResponse.toDeferredLink(): DeferredLink {
    val type = MatchType.fromWireName(matchType)
        ?: throw DleException.Malformed("resolve response carries unknown match_type '$matchType'")
    if (confidence.isNaN() || confidence.isInfinite()) {
        throw DleException.Malformed("resolve response has no valid confidence")
    }
    return DeferredLink(
        matched = matched,
        matchType = type,
        confidence = confidence.coerceIn(0.0, 1.0),
        clickId = clickId,
        link = link?.let { ResolvedLink(id = it.id, deeplinkPath = it.deeplinkPath, campaign = it.campaign, title = it.title) },
        params = params,
        expiresIn = if (expiresIn > 0) expiresIn else 0,
    )
}

/** The wire form of a public result, used to persist a cached resolve (TC-143). */
internal fun DeferredLink.toWire(): ResolveResponse = ResolveResponse(
    matched = matched,
    matchType = matchType.wireName,
    confidence = confidence,
    clickId = clickId,
    link = link?.let { LinkWire(id = it.id, deeplinkPath = it.deeplinkPath, campaign = it.campaign, title = it.title) },
    params = params,
    expiresIn = expiresIn,
)

/**
 * Translates a public event into an `EventDto`.
 *
 * @param nowMillis stamped as `ts` when the event carries no timestamp of its own, so the server
 * sees when the event happened rather than when the batch arrived.
 */
internal fun DleEvent.toWire(nowMillis: Long): EventWire = EventWire(
    type = type.wireName,
    name = name,
    url = url,
    value = value,
    currency = currency,
    ts = Iso8601.format(timestampMillis ?: nowMillis),
    properties = properties,
)

/**
 * The `consent` member of a resolve request, or `null` when nobody recorded a decision: a default
 * `{false, false}` would be indistinguishable from an explicit refusal, so it is not sent at all.
 */
internal fun DleConsent.toWire(): ConsentWire? {
    val ts = timestampMillis ?: return null
    return ConsentWire(analytics = analytics, attribution = attribution, ts = Iso8601.format(ts))
}
