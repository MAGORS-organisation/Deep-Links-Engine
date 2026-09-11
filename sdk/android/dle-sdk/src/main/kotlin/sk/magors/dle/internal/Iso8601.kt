package sk.magors.dle.internal

import java.util.Calendar
import java.util.GregorianCalendar
import java.util.Locale
import java.util.TimeZone

/**
 * Formats instants the way the engine serialises `DateTimeOffset`: ISO 8601 in UTC with an
 * explicit `+00:00` offset, for example `2026-09-03T10:00:00+00:00`. Milliseconds appear only when
 * they are non-zero, so the whole second case is byte identical to the contract literal in
 * `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`.
 *
 * `java.time` would be the natural tool, but on minSdk 23 it needs core library desugaring in
 * every consuming application, which is a dependency in all but name (spec §C.5).
 */
internal object Iso8601 {
    private val utc: TimeZone = TimeZone.getTimeZone("UTC")

    fun format(epochMillis: Long): String {
        val calendar = GregorianCalendar(utc, Locale.US)
        calendar.timeInMillis = epochMillis
        val base = String.format(
            Locale.US,
            "%04d-%02d-%02dT%02d:%02d:%02d",
            calendar.get(Calendar.YEAR),
            calendar.get(Calendar.MONTH) + 1,
            calendar.get(Calendar.DAY_OF_MONTH),
            calendar.get(Calendar.HOUR_OF_DAY),
            calendar.get(Calendar.MINUTE),
            calendar.get(Calendar.SECOND),
        )
        val millis = calendar.get(Calendar.MILLISECOND)
        return if (millis == 0) "$base+00:00" else String.format(Locale.US, "%s.%03d+00:00", base, millis)
    }
}
