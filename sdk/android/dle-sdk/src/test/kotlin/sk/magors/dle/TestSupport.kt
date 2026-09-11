package sk.magors.dle

import android.content.SharedPreferences
import sk.magors.dle.internal.Clock
import sk.magors.dle.internal.SdkLog
import java.util.Calendar
import java.util.GregorianCalendar
import java.util.Locale
import java.util.TimeZone

/** The install id used by every literal in `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`. */
internal const val CONTRACT_INSTALL_ID: String = "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1"

/** `2026-09-03T10:00:00Z`, the instant used everywhere a timestamp appears in the contract tests. */
internal val CONTRACT_MOMENT_MILLIS: Long = utcMillis(2026, 9, 3, 10, 0, 0)

/** Epoch milliseconds of a UTC wall clock time. */
internal fun utcMillis(year: Int, month: Int, day: Int, hour: Int, minute: Int, second: Int): Long {
    val calendar = GregorianCalendar(TimeZone.getTimeZone("UTC"), Locale.US)
    calendar.clear()
    calendar.set(year, month - 1, day, hour, minute, second)
    calendar.set(Calendar.MILLISECOND, 0)
    return calendar.timeInMillis
}

/** A log that discards everything. */
internal fun silentLog(): SdkLog = SdkLog(DleLogLevel.OFF, DleLogger { _, _, _ -> })

/** A log that records every line, for assertions about what is (not) logged. */
internal class RecordingLogger : DleLogger {
    val lines = ArrayList<String>()

    override fun log(level: DleLogLevel, message: String, throwable: Throwable?) {
        lines += "$level $message"
    }
}

/** A clock the test moves by hand. */
internal class ManualClock(var now: Long) : Clock {
    override fun nowMillis(): Long = now
}

/**
 * In-memory [SharedPreferences] so the stores can be tested on the JVM. Only the members the SDK
 * uses are meaningful; the rest satisfy the interface.
 */
internal class FakeSharedPreferences : SharedPreferences {
    private val values = HashMap<String, Any?>()

    override fun getAll(): MutableMap<String, *> = HashMap(values)

    override fun getString(key: String, defValue: String?): String? = values[key] as? String ?: defValue

    @Suppress("UNCHECKED_CAST")
    override fun getStringSet(key: String, defValues: MutableSet<String>?): MutableSet<String>? =
        values[key] as? MutableSet<String> ?: defValues

    override fun getInt(key: String, defValue: Int): Int = values[key] as? Int ?: defValue

    override fun getLong(key: String, defValue: Long): Long = values[key] as? Long ?: defValue

    override fun getFloat(key: String, defValue: Float): Float = values[key] as? Float ?: defValue

    override fun getBoolean(key: String, defValue: Boolean): Boolean = values[key] as? Boolean ?: defValue

    override fun contains(key: String): Boolean = values.containsKey(key)

    override fun edit(): SharedPreferences.Editor = Editor()

    override fun registerOnSharedPreferenceChangeListener(listener: SharedPreferences.OnSharedPreferenceChangeListener?) = Unit

    override fun unregisterOnSharedPreferenceChangeListener(listener: SharedPreferences.OnSharedPreferenceChangeListener?) = Unit

    private inner class Editor : SharedPreferences.Editor {
        private val pending = LinkedHashMap<String, Any?>()
        private var clearFirst = false

        override fun putString(key: String, value: String?): SharedPreferences.Editor = apply { pending[key] = value }

        override fun putStringSet(key: String, values: MutableSet<String>?): SharedPreferences.Editor =
            apply { pending[key] = values?.toMutableSet() }

        override fun putInt(key: String, value: Int): SharedPreferences.Editor = apply { pending[key] = value }

        override fun putLong(key: String, value: Long): SharedPreferences.Editor = apply { pending[key] = value }

        override fun putFloat(key: String, value: Float): SharedPreferences.Editor = apply { pending[key] = value }

        override fun putBoolean(key: String, value: Boolean): SharedPreferences.Editor = apply { pending[key] = value }

        override fun remove(key: String): SharedPreferences.Editor = apply { pending[key] = REMOVE }

        override fun clear(): SharedPreferences.Editor = apply { clearFirst = true }

        override fun commit(): Boolean {
            apply()
            return true
        }

        override fun apply() {
            if (clearFirst) values.clear()
            for ((key, value) in pending) {
                if (value === REMOVE || value == null) values.remove(key) else values[key] = value
            }
            pending.clear()
            clearFirst = false
        }
    }

    private companion object {
        val REMOVE = Any()
    }
}
