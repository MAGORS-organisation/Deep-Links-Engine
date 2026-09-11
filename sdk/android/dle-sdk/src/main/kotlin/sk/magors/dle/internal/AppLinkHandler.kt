package sk.magors.dle.internal

import android.content.Intent
import android.net.Uri
import sk.magors.dle.AppLinkOpen
import sk.magors.dle.DleEvent

/**
 * Turns an incoming `ACTION_VIEW` intent into a `link_open` report (FR-223, spec §B.6.4).
 *
 * A verified App Link opens the application straight from the operating system: no request ever
 * reaches the engine, so this report is the only evidence the click happened. Only `http` and
 * `https` data is reported; a custom scheme intent is ignored, because such links carry no
 * engine identifier and the SDK never transports anything over a custom scheme (FR-227).
 *
 * What is sent is the scheme, host and path. The query string and fragment are stripped on the
 * device: the engine does not use them and they may carry personal data (spec §E.6.3). The full
 * URL is never logged.
 *
 * @param enqueue where the event goes.
 * @param log the SDK log.
 */
internal class AppLinkHandler(
    private val enqueue: (DleEvent) -> Unit,
    private val log: SdkLog,
) {
    /**
     * Reports the intent's link when it is an `http(s)` `ACTION_VIEW`.
     *
     * The intent is marked once reported, so the same intent handed back by a configuration
     * change or by both `onCreate` and `onResume` produces one event, not several.
     *
     * @param intent the intent, or `null`.
     * @return the open, or `null` when the intent is not a reportable link.
     */
    fun handle(intent: Intent?): AppLinkOpen? {
        if (intent == null) return null
        return try {
            if (intent.action != Intent.ACTION_VIEW) return null
            val data = intent.data ?: return null
            val reported = reportedUrl(data) ?: return null
            val open = AppLinkOpen(url = data.toString(), reportedUrl = reported)
            if (intent.getBooleanExtra(EXTRA_REPORTED, false)) return open
            intent.putExtra(EXTRA_REPORTED, true)
            enqueue(DleEvent.linkOpen(reported))
            log.info("link_open reported for host ${data.host ?: "?"}")
            open
        } catch (e: RuntimeException) {
            // A hostile intent with an unparcelable extra must not crash the host.
            log.warn("intent could not be inspected", e)
            null
        }
    }

    internal companion object {
        /** Extra set on an intent once its link has been reported. */
        const val EXTRA_REPORTED: String = "sk.magors.dle.reported"

        /** The longest URL reported; anything longer is not a link a person clicked. */
        const val MAX_URL_LENGTH: Int = 2048

        /**
         * The reportable form of a URI: `scheme://host[:port]/path`, or `null` for a non http(s)
         * scheme, a missing host or an oversized value.
         */
        fun reportedUrl(uri: Uri): String? {
            val scheme = uri.scheme?.lowercase() ?: return null
            if (scheme != "https" && scheme != "http") return null
            if (!uri.isHierarchical) return null
            val host = uri.host?.lowercase()?.takeIf { it.isNotEmpty() } ?: return null
            val port = uri.port
            val path = uri.encodedPath?.takeIf { it.isNotEmpty() } ?: "/"
            val out = StringBuilder(scheme).append("://").append(host)
            if (port > 0 && port != defaultPort(scheme)) out.append(':').append(port)
            out.append(if (path.startsWith("/")) path else "/$path")
            if (out.length > MAX_URL_LENGTH) return null
            return out.toString()
        }

        private fun defaultPort(scheme: String): Int = if (scheme == "https") 443 else 80
    }
}
