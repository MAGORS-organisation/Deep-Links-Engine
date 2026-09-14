package sk.magors.dle.internal

import java.io.ByteArrayOutputStream

/**
 * Percent decodes a string the way `System.Uri.UnescapeDataString` does: `%XX` sequences become
 * bytes, the bytes are decoded as UTF-8 (invalid sequences become U+FFFD), a malformed escape is
 * left as it was, and `+` is left alone. `java.net.URLDecoder` is deliberately not used: it turns
 * `+` into a space, and click identifiers are opaque tokens that must survive byte for byte.
 */
internal fun percentDecode(value: String): String {
    if (value.isEmpty() || value.indexOf('%') < 0) return value
    val out = ByteArrayOutputStream(value.length)
    var i = 0
    var runStart = 0
    while (i < value.length) {
        if (value[i] == '%' && i + 2 < value.length) {
            val hi = Character.digit(value[i + 1], 16)
            val lo = Character.digit(value[i + 2], 16)
            if (hi >= 0 && lo >= 0) {
                if (runStart < i) out.write(value.substring(runStart, i).toByteArray(Charsets.UTF_8))
                out.write((hi shl 4) or lo)
                i += 3
                runStart = i
                continue
            }
        }
        i++
    }
    if (runStart < value.length) out.write(value.substring(runStart).toByteArray(Charsets.UTF_8))
    return String(out.toByteArray(), Charsets.UTF_8)
}

/**
 * Client side mirror of `Dle.Domain.Attribution.InstallReferrerParser` (spec §A.2.4).
 *
 * The raw value is a query string that Play returns URL encoded, and it is frequently encoded a
 * second time on the way in, so `dl_cid%3DaB3xK9pQ%26utm_source%3Dfb` is normal. The parser
 * therefore percent decodes the whole string once, splits it on `&`, splits each pair on the
 * *first* `=`, and percent decodes the key and the value once more. When a key occurs more than
 * once the first occurrence wins, so an appended second `dl_cid` cannot override the one the
 * engine wrote. It never throws.
 *
 * The SDK sends the referrer to the server verbatim (clipped to [MAX_REFERRER_LENGTH]); this
 * parser exists so the SDK can tell an organic installation from an attributed one locally and
 * so the behaviour is pinned by tests against the server's own examples.
 */
internal object InstallReferrerParser {
    /** Key under which the engine carries the click identifier into the store URL. */
    const val CLICK_ID_KEY: String = "dl_cid"

    /** Maximum number of characters of the raw referrer that are examined or transmitted. */
    const val MAX_REFERRER_LENGTH: Int = 1024

    /** Maximum number of key and value pairs returned by [parse]. */
    const val MAX_PAIRS: Int = 32

    /** Parses the referrer into its pairs. Malformed input yields an empty map. */
    fun parse(referrer: String?): Map<String, String> {
        if (referrer.isNullOrBlank()) return emptyMap()
        val truncated = referrer.length > MAX_REFERRER_LENGTH
        val bounded = if (truncated) referrer.substring(0, MAX_REFERRER_LENGTH) else referrer
        val segments = percentDecode(bounded).split('&')
        // A truncated input may have cut the final pair in half; dropping it is safer than
        // returning a value that is a prefix of the real one.
        val segmentCount = if (truncated && segments.size > 1) segments.size - 1 else segments.size
        val pairs = LinkedHashMap<String, String>()
        for (index in 0 until segmentCount) {
            if (pairs.size >= MAX_PAIRS) break
            val segment = segments[index]
            val separator = segment.indexOf('=')
            if (separator <= 0) continue
            val key = percentDecode(segment.substring(0, separator))
            if (key.isEmpty()) continue
            val value = percentDecode(segment.substring(separator + 1))
            if (!pairs.containsKey(key)) pairs[key] = value
        }
        return pairs
    }

    /**
     * Extracts the click identifier the engine wrote into the store URL (FR-181).
     *
     * @return the identifier, or `null` for an organic installation (TC-142).
     */
    fun clickId(referrer: String?): String? {
        val value = parse(referrer)[CLICK_ID_KEY] ?: return null
        val trimmed = value.trim()
        return trimmed.ifEmpty { null }
    }

    /** Clips a referrer to what the server will examine, so no bytes are sent for nothing. */
    fun clip(referrer: String?): String? = when {
        referrer.isNullOrEmpty() -> null
        referrer.length > MAX_REFERRER_LENGTH -> referrer.substring(0, MAX_REFERRER_LENGTH)
        else -> referrer
    }
}
