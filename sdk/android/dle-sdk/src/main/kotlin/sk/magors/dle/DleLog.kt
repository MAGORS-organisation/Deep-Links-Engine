package sk.magors.dle

import android.util.Log

/** Verbosity of the SDK's diagnostic output. */
public enum class DleLogLevel {
    /** No output at all. */
    OFF,

    /** Failures only. */
    ERROR,

    /** Failures and recoverable problems. The default. */
    WARN,

    /** Lifecycle milestones: initialised, resolved, flushed. */
    INFO,

    /** Everything, including per request outcomes. Never enable in a shipping build. */
    DEBUG,
}

/**
 * A destination for SDK log lines. Supply one through [DleConfig.Builder.logger] to bridge into the
 * host application's logging stack; the default writes to `android.util.Log` under the tag `DLE`.
 *
 * Hard rules the SDK keeps regardless of the sink: it never logs the SDK key or any
 * `Authorization` value, never a full URL including its query string, never a raw install id
 * (only a short prefix) and never a full Install Referrer. These mirror the server's own logging
 * prohibitions (spec S-08, SHARED-KERNEL §17.5).
 */
public fun interface DleLogger {
    /**
     * Receives one line.
     *
     * @param level severity of the line; already filtered against [DleConfig.logLevel].
     * @param message the text, free of secrets and identifiers.
     * @param throwable an accompanying exception, when there is one.
     */
    public fun log(level: DleLogLevel, message: String, throwable: Throwable?)
}

/** The default sink: `android.util.Log` with tag `DLE`. */
public object AndroidDleLogger : DleLogger {
    private const val TAG = "DLE"

    override fun log(level: DleLogLevel, message: String, throwable: Throwable?) {
        when (level) {
            DleLogLevel.OFF -> Unit
            DleLogLevel.ERROR -> Log.e(TAG, message, throwable)
            DleLogLevel.WARN -> Log.w(TAG, message, throwable)
            DleLogLevel.INFO -> Log.i(TAG, message, throwable)
            DleLogLevel.DEBUG -> Log.d(TAG, message, throwable)
        }
    }
}
