package sk.magors.dle.internal

import sk.magors.dle.DleLogLevel
import sk.magors.dle.DleLogger

/**
 * Level filtered logger in front of the configured [DleLogger].
 *
 * Nothing that passes through here may contain the SDK key, an `Authorization` value, a full URL
 * with its query string, a raw install id or a raw Install Referrer. Use [redact] for identifiers
 * a support engineer needs to correlate but must not be able to re-identify with.
 */
internal class SdkLog(
    private val level: DleLogLevel,
    private val sink: DleLogger,
) {
    fun error(message: String, throwable: Throwable? = null) = emit(DleLogLevel.ERROR, message, throwable)

    fun warn(message: String, throwable: Throwable? = null) = emit(DleLogLevel.WARN, message, throwable)

    fun info(message: String, throwable: Throwable? = null) = emit(DleLogLevel.INFO, message, throwable)

    fun debug(message: String, throwable: Throwable? = null) = emit(DleLogLevel.DEBUG, message, throwable)

    fun isEnabled(candidate: DleLogLevel): Boolean =
        candidate != DleLogLevel.OFF && level != DleLogLevel.OFF && candidate.ordinal <= level.ordinal

    private fun emit(candidate: DleLogLevel, message: String, throwable: Throwable?) {
        if (!isEnabled(candidate)) return
        try {
            sink.log(candidate, message, throwable)
        } catch (_: RuntimeException) {
            // A logger that throws must not take the SDK down with it.
        }
    }

    companion object {
        /**
         * Shortens a value so it can appear in a log line without disclosing it: the first
         * [keep] characters plus the length.
         */
        fun redact(value: String?, keep: Int = 6): String = when {
            value.isNullOrEmpty() -> "<empty>"
            value.length <= keep -> "<redacted:${value.length}>"
            else -> "${value.substring(0, keep)}…<redacted:${value.length}>"
        }
    }
}

/** Source of the current time, injectable for tests. */
internal fun interface Clock {
    fun nowMillis(): Long

    companion object {
        val SYSTEM: Clock = Clock { System.currentTimeMillis() }
    }
}
