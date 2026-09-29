package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.net.IsoInstant
import java.text.DateFormat
import java.util.Calendar
import java.util.Date

/**
 * Label for one entry of the Server terminal session strip.
 *
 * The Server sends creation time as ISO-8601 UTC. A session started today is identified by its time
 * alone, while an older one keeps its date, so a chip can never read as "just now" when it is not.
 * The id suffix is what actually disambiguates two sessions of the same day.
 *
 * An unparsable timestamp yields the id alone — the strip never shows a guessed time.
 */
internal fun terminalSessionLabel(createdAt: String, sessionId: String, nowMillis: Long): String {
    val shortId = sessionId.take(8)
    val createdMillis = IsoInstant.toEpochMillis(createdAt) ?: return shortId

    val created = Calendar.getInstance().apply { timeInMillis = createdMillis }
    val now = Calendar.getInstance().apply { timeInMillis = nowMillis }
    val sameDay = created.get(Calendar.YEAR) == now.get(Calendar.YEAR) &&
        created.get(Calendar.DAY_OF_YEAR) == now.get(Calendar.DAY_OF_YEAR)

    val formatter = if (sameDay) {
        DateFormat.getTimeInstance(DateFormat.MEDIUM)
    } else {
        DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT)
    }
    return "${formatter.format(Date(createdMillis))} · $shortId"
}
