package sk.magors.dle.sample

import android.content.Intent
import android.os.Bundle
import android.util.TypedValue
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import sk.magors.dle.DeepLinkAllowlist
import sk.magors.dle.DeferredLink
import sk.magors.dle.Dle
import sk.magors.dle.DleConsent
import sk.magors.dle.DleEvent
import sk.magors.dle.DleException
import sk.magors.dle.SafeDeepLink
import sk.magors.dle.dleConfig

/**
 * The one screen of the sample. It initialises the SDK (a no-op after [SampleApplication]),
 * resolves the deferred link, and shows whatever App Link opened the activity, after the link has
 * passed the [DeepLinkAllowlist].
 *
 * Security notes for anyone copying this:
 *
 * - A deep link is untrusted input even though the operating system delivered it. Any web page
 *   can craft `https://link.example.sk/anything?x=y`, and it will reach `onCreate` or
 *   `onNewIntent` here. The activity never routes on the raw `intent.data`; it routes on the
 *   [SafeDeepLink] the allowlist returns, or not at all (spec §E.7, MASTG-TEST-0028).
 * - The same discipline applies to the `deeplink_path` the engine returns: it was typed by an
 *   operator in the console, so it goes through [DeepLinkAllowlist.parsePath] first.
 * - "Dirty Stream" (Microsoft, 2024): an exported activity that receives a `content://` URI in an
 *   `ACTION_VIEW` or `ACTION_SEND` intent and copies the stream to a path taken from the
 *   provider's reported file name lets a malicious application overwrite files in the app's own
 *   sandbox (`../shared_prefs/…`). This activity accepts `https` only, never opens `intent.data`
 *   as a stream, and never derives a file path from anything in an intent. Keep it that way.
 */
class MainActivity : ComponentActivity() {
    /**
     * Everything this application is willing to open. Exact host, path prefixes on a segment
     * boundary, named query parameters; the rest of a URL is discarded.
     */
    private val allowlist: DeepLinkAllowlist = DeepLinkAllowlist.builder()
        .host("link.example.sk")
        .pathPrefix("/promo", "/p")
        .param("promo", "utm_campaign")
        .build()

    private lateinit var deferredView: TextView
    private lateinit var openView: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        // Idempotent: SampleApplication already did this. Shown because the brief of an
        // integration guide is "initialise, then resolve", and both belong on the first screen.
        Dle.initialize(this, dleConfig(BuildConfig.DLE_ENDPOINT, BuildConfig.DLE_SDK_KEY))

        showOpen(intent)
        resolve()
    }

    /**
     * A link tapped while this singleTop activity is already on screen arrives here. Calling
     * `setIntent` matters: the SDK's automatic `link_open` reporting reads `activity.intent` on
     * resume, and without it the new link would be neither shown nor reported.
     */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        showOpen(intent)
    }

    private fun resolve() {
        deferredView.setText(R.string.status_resolving)
        lifecycleScope.launch {
            try {
                showDeferred(Dle.resolve())
            } catch (e: DleException) {
                deferredView.text = getString(R.string.status_resolve_failed, "${e.javaClass.simpleName}: ${e.message}")
            }
        }
    }

    private fun showDeferred(link: DeferredLink) {
        val safePath: SafeDeepLink? = allowlist.parsePath(link.link?.deeplinkPath)
        deferredView.text = buildString {
            appendLine(getString(R.string.heading_deferred))
            appendLine("matched: ${link.matched}")
            appendLine("match_type: ${link.matchType.wireName}")
            appendLine("confidence: ${link.confidence}")
            appendLine("deterministic: ${link.isDeterministic}")
            appendLine("click_id: ${link.clickId ?: "-"}")
            appendLine("link: ${link.link?.id ?: "-"} ${link.link?.campaign ?: ""}")
            appendLine("deeplink_path (raw): ${link.link?.deeplinkPath ?: "-"}")
            appendLine("deeplink_path (allowlisted): ${safePath?.path ?: "rejected"}")
            appendLine("params: ${link.params}")
        }
        // Only a deterministic match may drive navigation; a probabilistic one is a hint at most.
        if (link.isDeterministic && safePath != null) {
            navigateTo(safePath)
        }
    }

    private fun showOpen(intent: Intent?) {
        // The SDK reports the open (autoReportOpens); calling handleIntent again is harmless and
        // hands back the URL so the activity can validate and route on it.
        val open = Dle.handleIntent(intent)
        if (open == null) {
            openView.text = ""
            return
        }
        val safe = allowlist.parse(open.url)
        openView.text = buildString {
            appendLine(getString(R.string.heading_open))
            appendLine("reported as: ${open.reportedUrl}")
            appendLine(
                if (safe == null) {
                    getString(R.string.open_rejected, open.reportedUrl)
                } else {
                    getString(R.string.open_accepted, safe.host, safe.path, safe.params.toString())
                },
            )
        }
        if (safe != null) navigateTo(safe)
    }

    private fun navigateTo(link: SafeDeepLink) {
        // A real application starts the matching screen here. The path and params are already
        // validated, so a router may switch on link.path without further checks.
        Dle.trackEvent(DleEvent.custom("sample_navigate", mapOf("path" to link.path)))
    }

    private fun buildLayout(): ScrollView {
        val padding = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, 16f, resources.displayMetrics).toInt()
        deferredView = TextView(this).apply {
            setTextIsSelectable(true)
            setText(R.string.status_initialising)
        }
        openView = TextView(this).apply { setTextIsSelectable(true) }
        val grant = Button(this).apply {
            text = "Grant consent"
            setOnClickListener { Dle.setConsent(DleConsent.granted()) }
        }
        val deny = Button(this).apply {
            text = "Deny consent"
            setOnClickListener { Dle.setConsent(DleConsent.denied().copy(timestampMillis = System.currentTimeMillis())) }
        }
        val conversion = Button(this).apply {
            text = "Track a conversion"
            setOnClickListener { Dle.trackEvent(DleEvent.conversion("purchase", 24.9, "EUR")) }
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
            addView(deferredView)
            addView(openView)
            addView(grant)
            addView(deny)
            addView(conversion)
        }
        return ScrollView(this).apply { addView(column) }
    }
}
