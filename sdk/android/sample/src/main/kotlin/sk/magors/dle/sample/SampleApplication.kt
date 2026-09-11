package sk.magors.dle.sample

import android.app.Application
import sk.magors.dle.Dle
import sk.magors.dle.DleConsent
import sk.magors.dle.DleLogLevel
import sk.magors.dle.dleConfig

/**
 * Initialises the SDK once, before any activity exists. Doing it here rather than in an activity
 * lets the SDK observe the activity lifecycle from the very first `onCreate`, so an App Link that
 * cold starts the application is reported as `link_open` (FR-223). [MainActivity] calls
 * [Dle.initialize] as well; the second call is a no-op that returns the same client.
 */
class SampleApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        Dle.initialize(
            this,
            dleConfig(BuildConfig.DLE_ENDPOINT, BuildConfig.DLE_SDK_KEY) {
                // Nothing is consented until the person decides. The sample asks on its first screen.
                consent(DleConsent.denied())
                // Off by default in the SDK too; shown here so the choice is visible.
                probabilisticSignals(false)
                logLevel(if (BuildConfig.DEBUG) DleLogLevel.DEBUG else DleLogLevel.WARN)
            },
        )
    }
}
