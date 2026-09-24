package sk.magors.dle

import sk.magors.dle.internal.percentDecode
import java.net.URI
import java.net.URISyntaxException

/**
 * An App Link open the SDK reported (FR-223), handed back so the application can route on it.
 *
 * @property url the URL exactly as the operating system delivered it. Untrusted input, even
 * though it came from the OS (MASTG-TEST-0028): pass it through a [DeepLinkAllowlist] before
 * navigating.
 * @property reportedUrl what was sent to the engine as the `link_open` URL: scheme, host and path
 * only. The query string and fragment are dropped on the device because the engine never uses
 * them and they may carry personal data (spec §E.6.3).
 */
public data class AppLinkOpen(
    public val url: String,
    public val reportedUrl: String,
)

/**
 * A deep link that passed a [DeepLinkAllowlist].
 *
 * @property host lower cased host, or an empty string for a path only link.
 * @property path the decoded, normalised path, always starting with `/`.
 * @property params allow-listed query parameters, decoded. First occurrence wins.
 */
public data class SafeDeepLink(
    public val host: String,
    public val path: String,
    public val params: Map<String, String>,
)

/**
 * Allowlist based deep link parser (spec §E.7 rule 1).
 *
 * A deep link is untrusted input even when the operating system delivered it: any application can
 * register the same custom scheme, a web page can craft an App Link with any path and any query,
 * and the engine's `deeplink_path` was typed by an operator. This parser accepts a link only when
 * every part of it is on a list you wrote down:
 *
 * - the scheme is `https`,
 * - the host is one of [Builder.host] exactly (no wildcards, no suffix matching),
 * - the path starts with one of [Builder.pathPrefix] on a segment boundary and contains no `.`,
 *   `..` or empty segments and no control characters,
 * - only [Builder.param] query parameters survive, each at most [Builder.maxParamLength]
 *   characters, and
 * - the fragment is discarded.
 *
 * Anything else yields `null`. Route on the result, never on the raw URL.
 */
public class DeepLinkAllowlist private constructor(
    private val hosts: Set<String>,
    private val pathPrefixes: List<String>,
    private val params: Set<String>,
    private val maxParamLength: Int,
) {
    /**
     * Validates an absolute URL.
     *
     * @param url the URL as received.
     * @return the safe link, or `null` when any rule fails.
     */
    public fun parse(url: String?): SafeDeepLink? {
        if (url.isNullOrBlank() || url.length > MAX_URL_LENGTH) return null
        val uri = try {
            URI(url.trim())
        } catch (_: URISyntaxException) {
            return null
        }
        if (!uri.isAbsolute || uri.scheme?.lowercase() != "https") return null
        val host = uri.host?.lowercase() ?: return null
        if (host !in hosts) return null
        if (uri.port != -1 && uri.port != HTTPS_PORT) return null
        if (uri.rawUserInfo != null) return null
        val path = safePath(uri.rawPath) ?: return null
        return SafeDeepLink(host = host, path = path, params = safeParams(uri.rawQuery))
    }

    /**
     * Validates a path only value such as [ResolvedLink.deeplinkPath], optionally carrying a
     * query string.
     *
     * @param path the path as received, for example `/promo/autumn?promo=AUTUMN20`.
     * @return the safe link with an empty host, or `null` when any rule fails.
     */
    public fun parsePath(path: String?): SafeDeepLink? {
        if (path.isNullOrBlank() || path.length > MAX_URL_LENGTH) return null
        val trimmed = path.trim()
        if (trimmed.contains("://") || !trimmed.startsWith("/")) return null
        val split = trimmed.indexOf('?')
        val rawPath = if (split < 0) trimmed else trimmed.substring(0, split)
        val rawQuery = if (split < 0) null else trimmed.substring(split + 1)
        val safe = safePath(rawPath) ?: return null
        return SafeDeepLink(host = "", path = safe, params = safeParams(rawQuery))
    }

    private fun safePath(rawPath: String?): String? {
        val raw = if (rawPath.isNullOrEmpty()) "/" else rawPath
        if (raw.length > MAX_URL_LENGTH || raw.indexOf('#') >= 0) return null
        var segments = raw.removePrefix("/").split('/')
        // A trailing slash is tolerated; an empty segment anywhere else is not.
        if (segments.size > 1 && segments.last().isEmpty()) segments = segments.dropLast(1)
        val decoded = ArrayList<String>(segments.size)
        for (segment in segments) {
            val value = percentDecode(segment)
            if (value.isEmpty() && segments.size > 1) return null
            if (value == "." || value == ".." || value.indexOf('/') >= 0 || value.indexOf('\\') >= 0) return null
            if (value.any { it.isISOControl() }) return null
            decoded += value
        }
        val path = "/" + decoded.joinToString("/")
        val allowed = pathPrefixes.any { prefix ->
            prefix == "/" || path == prefix || path.startsWith("$prefix/")
        }
        return if (allowed) path else null
    }

    private fun safeParams(rawQuery: String?): Map<String, String> {
        if (rawQuery.isNullOrEmpty() || params.isEmpty()) return emptyMap()
        val out = LinkedHashMap<String, String>()
        for (pair in rawQuery.split('&')) {
            val separator = pair.indexOf('=')
            if (separator <= 0) continue
            val key = percentDecode(pair.substring(0, separator))
            if (key !in params || out.containsKey(key)) continue
            val value = percentDecode(pair.substring(separator + 1))
            if (value.length > maxParamLength || value.any { it.isISOControl() }) continue
            out[key] = value
        }
        return out
    }

    /** Builder for [DeepLinkAllowlist]. */
    public class Builder {
        private val hosts = LinkedHashSet<String>()
        private val pathPrefixes = ArrayList<String>()
        private val params = LinkedHashSet<String>()
        private var maxParamLength = DEFAULT_MAX_PARAM_LENGTH

        /**
         * Allows hosts, matched exactly and case insensitively.
         *
         * @param values host names such as `link.example.sk`.
         */
        public fun host(vararg values: String): Builder = apply {
            for (value in values) hosts += value.trim().lowercase()
        }

        /**
         * Allows paths starting with these prefixes on a segment boundary. `/` allows every path.
         *
         * @param values prefixes such as `/promo`.
         */
        public fun pathPrefix(vararg values: String): Builder = apply {
            for (value in values) {
                val prefix = value.trim().trimEnd('/')
                pathPrefixes += if (prefix.isEmpty()) "/" else if (prefix.startsWith("/")) prefix else "/$prefix"
            }
        }

        /**
         * Allows query parameters by exact name. Everything else is dropped.
         *
         * @param names parameter names such as `promo`.
         */
        public fun param(vararg names: String): Builder = apply {
            for (name in names) params += name.trim()
        }

        /**
         * Longest accepted parameter value. Default 256.
         *
         * @param value the bound, at least 1.
         */
        public fun maxParamLength(value: Int): Builder = apply {
            require(value >= 1) { "maxParamLength must be at least 1" }
            maxParamLength = value
        }

        /**
         * Builds the allowlist.
         *
         * @throws IllegalStateException when no host and no path prefix was allowed.
         */
        public fun build(): DeepLinkAllowlist {
            check(hosts.isNotEmpty()) { "allow at least one host" }
            check(pathPrefixes.isNotEmpty()) { "allow at least one path prefix (\"/\" allows every path)" }
            return DeepLinkAllowlist(hosts.toSet(), pathPrefixes.toList(), params.toSet(), maxParamLength)
        }
    }

    public companion object {
        /** Longest URL looked at, mirroring the engine's `TargetUrlPolicy.MaxUrlLength`. */
        public const val MAX_URL_LENGTH: Int = 2048

        /** Default [Builder.maxParamLength]. */
        public const val DEFAULT_MAX_PARAM_LENGTH: Int = 256

        private const val HTTPS_PORT = 443

        /** Starts a builder. */
        @JvmStatic
        public fun builder(): Builder = Builder()
    }
}
