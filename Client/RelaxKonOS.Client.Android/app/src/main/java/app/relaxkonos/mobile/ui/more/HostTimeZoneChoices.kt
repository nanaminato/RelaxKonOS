package app.relaxkonos.mobile.ui.more

import java.time.Instant
import java.time.ZoneId
import java.util.Locale
import kotlin.math.abs

/**
 * The suggestion list and the offset labels of the host time-zone editor.
 *
 * This lives outside the composable because the interesting part is a decision, not a layout: the
 * host answers with a few hundred IDs in its own order, and offering their alphabetical head — which
 * is what the field used to do — tells the user nothing. Keeping the choice here also makes the
 * ranking and the featured set unit-testable, which a `@Composable` is not.
 */
internal object HostTimeZoneChoices {
    /** How many filtered IDs the inline list shows at once; the page documentation promises twelve. */
    const val limit = 12

    /**
     * The IANA areas, in the order the picker lists them.
     *
     * This is the database's own first segment rather than a translation table: the heading of a group
     * and the prefix of the IDs under it have to agree, and only the ID itself can guarantee that.
     */
    private val areas = listOf("Africa", "America", "Antarctica", "Arctic", "Asia", "Atlantic",
        "Australia", "Europe", "Indian", "Pacific", "Etc")

    /** The heading for IDs with no area segment, such as `UTC`, and for areas this build does not know. */
    const val otherArea = "other"

    /** One picker heading with the IDs under it, in the order the host sent them. */
    data class Region(val area: String, val zones: List<String>)

    /**
     * Every ID the host listed, grouped by area, empty groups dropped.
     *
     * The picker needs this because search alone is not a choice: a user who does not know that
     * Shanghai lives under `Asia` — or that the host's list has 419 entries — has nothing to type.
     * Grouping keeps a few hundred rows scannable while leaving the host's ordering intact inside a
     * group, so the same data reads the same way here as it does in the field's filter.
     */
    fun regions(zones: List<String>): List<Region> =
        (areas + otherArea).mapNotNull { area ->
            val members = zones.filter { areaOf(it) == area }
            if (members.isEmpty()) null else Region(area, members)
        }

    /**
     * Zero-offset IDs, in the spellings hosts actually use.
     *
     * Not derived from an offset calculation on purpose: `Africa/Abidjan` is UTC+00:00 too, and
     * picking it to mean "no offset" would be a coincidence rather than a choice.
     */
    private val universal = listOf("UTC", "Etc/UTC", "Etc/GMT", "GMT")

    /**
     * The IDs to offer under the field, best first.
     *
     * An empty query is not a search, so it gets the three IDs this page is actually about: what the
     * host runs now, what this phone runs now, and a zero-offset ID. That answer is short, and it
     * covers both reasons to be on this page — the host clock is wrong, or it disagrees with the
     * phone. A non-empty query searches the host's whole list instead, exact matches first.
     *
     * Only IDs the host listed are ever returned: previewing anything else is refused by the server,
     * so offering it would be a dead end.
     */
    fun suggestions(zones: List<String>, query: String, hostZone: String, deviceZone: String): List<String> {
        val needle = query.trim()
        if (needle.isEmpty()) {
            return (listOf(hostZone, deviceZone) + universal)
                .filter { it.isNotEmpty() && it in zones }
                .distinct()
                .take(limit)
        }
        return matches(zones, needle).take(limit)
    }

    /**
     * Every ID the query matches, best first and without the [limit].
     *
     * The picker has a screen of its own, so it can afford to show a whole area's worth of matches
     * instead of the twelve the inline list can fit; the ranking is the same, which is why it lives
     * here rather than being written twice.
     */
    fun matches(zones: List<String>, query: String): List<String> {
        val needle = query.trim()
        if (needle.isEmpty()) return emptyList()
        return zones.filter { it.contains(needle, ignoreCase = true) }
            .sortedWith(compareBy<String> { rank(it, needle) }.then(String.CASE_INSENSITIVE_ORDER))
    }

    /** The area segment of an ID, or [otherArea] when there is none this build recognises. */
    private fun areaOf(zone: String) = zone.substringBefore('/', "").takeIf { it in areas } ?: otherArea

    /** Every ID the query matches, including the ones past [limit] that the list cannot show. */
    fun matchCount(zones: List<String>, query: String): Int {
        val needle = query.trim()
        return if (needle.isEmpty()) 0 else zones.count { it.contains(needle, ignoreCase = true) }
    }

    /**
     * Exact ID, then IDs that begin with the query, then IDs whose city segment does.
     *
     * The city rule is what makes `shanghai` reach `Asia/Shanghai`: nobody types the continent, and
     * under a plain "contains" sort every other Asia zone would outrank it.
     */
    private fun rank(zone: String, needle: String): Int = when {
        zone.equals(needle, ignoreCase = true) -> 0
        zone.startsWith(needle, ignoreCase = true) -> 1
        zone.substringAfter('/').startsWith(needle, ignoreCase = true) -> 2
        else -> 3
    }

    /**
     * `UTC+08:00` for an ID, or `null` when this phone cannot resolve an ID the host offered.
     *
     * The offset is what makes a bare ID readable: `Etc/UTC` and `Pacific/Kiritimati` are the same
     * kind of string, and only the offset says which one the host is actually on. A host whose tzdata
     * is newer than the phone's can name an ID this build does not know, and that is a missing label
     * rather than a failure.
     */
    fun offsetLabel(zoneId: String, atEpochMillis: Long): String? {
        val minutes = runCatching {
            ZoneId.of(zoneId).rules.getOffset(Instant.ofEpochMilli(atEpochMillis)).totalSeconds / 60
        }.getOrNull() ?: return null
        val absolute = abs(minutes)
        return "UTC${if (minutes < 0) "-" else "+"}%02d:%02d".format(Locale.ROOT, absolute / 60, absolute % 60)
    }
}
