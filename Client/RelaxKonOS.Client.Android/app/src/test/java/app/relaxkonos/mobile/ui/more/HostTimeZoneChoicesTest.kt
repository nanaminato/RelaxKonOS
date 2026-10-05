package app.relaxkonos.mobile.ui.more

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** The host editor offers these IDs; the screenshot regression was the alphabetical head of them. */
private val ZONES = listOf(
    "Africa/Abidjan", "Africa/Accra", "Africa/Addis_Ababa", "Africa/Algiers",
    "America/Sao_Paulo", "America/St_Johns",
    "Asia/Kathmandu", "Asia/Shanghai", "Asia/Tokyo",
    "Etc/UTC", "Europe/London", "Pacific/Kiritimati",
)

/** 2026-01-15T00:00:00Z, an instant with standard offsets in both hemispheres. */
private const val WINTER = 1768435200000L

class HostTimeZoneChoicesTest {
    @Test fun `an empty query offers the host zone then the phone zone then zero offset`() {
        assertEquals(
            listOf("Asia/Tokyo", "Asia/Shanghai", "Etc/UTC"),
            HostTimeZoneChoices.suggestions(ZONES, "", "Asia/Tokyo", "Asia/Shanghai"),
        )
    }

    @Test fun `a host already on the phone zone is not offered twice`() {
        assertEquals(
            listOf("Asia/Tokyo", "Etc/UTC"),
            HostTimeZoneChoices.suggestions(ZONES, "", "Asia/Tokyo", "Asia/Tokyo"),
        )
    }

    @Test fun `an id the host does not list is never a suggestion`() {
        assertEquals(
            listOf("Etc/UTC"),
            HostTimeZoneChoices.suggestions(ZONES, "", "Etc/GMT+9", "Europe/Paris"),
        )
    }

    @Test fun `whitespace is not a query`() {
        assertEquals(
            HostTimeZoneChoices.suggestions(ZONES, "", "Asia/Tokyo", "Asia/Shanghai"),
            HostTimeZoneChoices.suggestions(ZONES, "   ", "Asia/Tokyo", "Asia/Shanghai"),
        )
        assertEquals(0, HostTimeZoneChoices.matchCount(ZONES, "   "))
    }

    @Test fun `an exact id outranks a city and a city outranks a mere match`() {
        val zones = listOf("Asia/Shanghai", "Asia/Shanghai_North", "Shanghai", "America/New_York_Shanghai")
        assertEquals(
            listOf("Shanghai", "Asia/Shanghai", "Asia/Shanghai_North", "America/New_York_Shanghai"),
            HostTimeZoneChoices.suggestions(zones, "shanghai", "Etc/UTC", "Etc/UTC"),
        )
    }

    @Test fun `a city segment is searchable without the continent`() {
        assertEquals(
            listOf("Asia/Shanghai"),
            HostTimeZoneChoices.suggestions(ZONES, "shang", "Etc/UTC", "Etc/UTC"),
        )
    }

    @Test fun `a continent query keeps the host ordering under an alphabetical sort`() {
        assertEquals(
            listOf("Africa/Abidjan", "Africa/Accra", "Africa/Addis_Ababa", "Africa/Algiers"),
            HostTimeZoneChoices.suggestions(ZONES, "africa/a", "Etc/UTC", "Etc/UTC"),
        )
    }

    @Test fun `matches are capped but counted in full`() {
        val zones = (1..30).map { "Test/Zone%02d".format(java.util.Locale.ROOT, it) }
        val shown = HostTimeZoneChoices.suggestions(zones, "Test/", "Etc/UTC", "Etc/UTC")
        assertEquals(HostTimeZoneChoices.limit, shown.size)
        assertEquals("Test/Zone01", shown.first())
        assertEquals(30, HostTimeZoneChoices.matchCount(zones, "test/"))
    }

    @Test fun `a query that matches nothing yields nothing`() {
        assertEquals(emptyList<String>(), HostTimeZoneChoices.suggestions(ZONES, "Mars/Olympus", "Etc/UTC", "Etc/UTC"))
        assertEquals(0, HostTimeZoneChoices.matchCount(ZONES, "Mars/Olympus"))
    }

    @Test fun `offsets are labelled in hours and minutes`() {
        assertEquals("UTC+00:00", HostTimeZoneChoices.offsetLabel("Etc/UTC", WINTER))
        assertEquals("UTC+08:00", HostTimeZoneChoices.offsetLabel("Asia/Shanghai", WINTER))
        assertEquals("UTC+05:45", HostTimeZoneChoices.offsetLabel("Asia/Kathmandu", WINTER))
        assertEquals("UTC+14:00", HostTimeZoneChoices.offsetLabel("Pacific/Kiritimati", WINTER))
        assertEquals("UTC-03:00", HostTimeZoneChoices.offsetLabel("America/Sao_Paulo", WINTER))
    }

    @Test fun `a half hour offset keeps its sign`() {
        assertEquals("UTC-03:30", HostTimeZoneChoices.offsetLabel("America/St_Johns", WINTER))
    }

    @Test fun `an id this phone cannot resolve has no label`() {
        assertNull(HostTimeZoneChoices.offsetLabel("Mars/Olympus", WINTER))
        assertNull(HostTimeZoneChoices.offsetLabel("", WINTER))
    }

    @Test fun `every listed id lands in exactly one area group`() {
        val grouped = HostTimeZoneChoices.regions(ZONES)
        assertEquals(listOf("Africa", "America", "Asia", "Europe", "Pacific", "Etc"), grouped.map { it.area })
        assertEquals(ZONES.sorted(), grouped.flatMap { it.zones }.sorted())
    }

    @Test fun `the host ordering survives inside a group`() {
        assertEquals(
            listOf("Africa/Abidjan", "Africa/Accra", "Africa/Addis_Ababa", "Africa/Algiers"),
            HostTimeZoneChoices.regions(ZONES).first { it.area == "Africa" }.zones,
        )
    }

    @Test fun `an id without a known area is grouped instead of dropped`() {
        val grouped = HostTimeZoneChoices.regions(listOf("UTC", "Mars/Olympus", "Test/Zone01", "Asia/Tokyo"))
        assertEquals(listOf("Asia", HostTimeZoneChoices.otherArea), grouped.map { it.area })
        assertEquals(listOf("UTC", "Mars/Olympus", "Test/Zone01"), grouped.last().zones)
    }

    @Test fun `an area with no members is not a heading`() {
        assertEquals(emptyList<String>(), HostTimeZoneChoices.regions(emptyList<String>()).map { it.area })
        assertEquals(listOf("Etc"), HostTimeZoneChoices.regions(listOf("Etc/UTC")).map { it.area })
    }

    @Test fun `the picker lists every match while the field keeps its cap`() {
        val zones = (1..30).map { "Test/Zone%02d".format(java.util.Locale.ROOT, it) }
        assertEquals(30, HostTimeZoneChoices.matches(zones, "test/").size)
        assertEquals(HostTimeZoneChoices.limit, HostTimeZoneChoices.suggestions(zones, "test/", "Etc/UTC", "Etc/UTC").size)
    }

    @Test fun `the picker searches with the field ranking`() {
        val zones = listOf("Asia/Shanghai", "Shanghai", "America/New_York_Shanghai")
        assertEquals(listOf("Shanghai", "Asia/Shanghai", "America/New_York_Shanghai"), HostTimeZoneChoices.matches(zones, "shanghai"))
    }

    @Test fun `a blank query is not a picker search`() {
        assertEquals(emptyList<String>(), HostTimeZoneChoices.matches(ZONES, "   "))
    }
}
