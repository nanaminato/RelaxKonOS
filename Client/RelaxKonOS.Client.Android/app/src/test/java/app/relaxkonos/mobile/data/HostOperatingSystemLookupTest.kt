package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.security.model.SavedLogin
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Filling in the host mark of a saved connection.
 *
 * The rules under test are the ones that keep opening a dialog from becoming a scan of every saved
 * server, and the one that keeps "could not ask" from hardening into a permanent answer.
 */
class HostOperatingSystemLookupTest {
    private val profiles = ConnectionProfileStore(InMemoryProfileStorage())
    private val gateway = FakeGateway()
    private val lookup = HostOperatingSystemLookup(profiles, gateway)

    private val alpha = SavedLogin("https://alpha:5090", "nana", 100L)
    private val managed = SavedLogin("rki-0123456789abcdef0123456789abcdef", "nana", 300L)

    @Test
    fun `an entry nobody has asked is asked and keeps the answer`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(1, lookup.resolve(profiles.all()))

        assertEquals(listOf("https://alpha:5090"), gateway.hostOperatingSystemLookups)
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `an entry that already knows its mark is never asked`() = runTest {
        profiles.upsert(alpha.copy(hostOperatingSystem = HostOperatingSystemKind.Windows11))
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(0, lookup.resolve(profiles.all()))

        assertTrue(gateway.hostOperatingSystemLookups.isEmpty())
        assertEquals(HostOperatingSystemKind.Windows11, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `an answer of unknown is final rather than missing`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Success(HostOperatingSystemKind.Unknown))

        assertEquals(1, lookup.resolve(profiles.all()))
        assertEquals(0, lookup.resolve(profiles.all()))

        assertEquals(1, gateway.hostOperatingSystemLookups.size)
        assertEquals(HostOperatingSystemKind.Unknown, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a failed lookup is not an answer, so the next open can try again`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Transport("no route to host"))

        assertEquals(0, lookup.resolve(profiles.all()))
        assertNull(profiles.all().single().hostOperatingSystem)

        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))
        assertEquals(1, lookup.resolve(profiles.all()))
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a refused lookup is not stored either`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Problem(status = 404, code = "not-found", traceId = null))

        assertEquals(0, lookup.resolve(profiles.all()))

        assertNull(profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a managed login is never asked, because its address exists only while its host is open`() = runTest {
        profiles.upsert(managed)
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(0, lookup.resolve(profiles.all()))

        assertTrue(gateway.hostOperatingSystemLookups.isEmpty())
    }

    @Test
    fun `every unasked entry is asked, whatever the parallel limit is`() = runTest {
        (1..6).forEach { index -> profiles.upsert(SavedLogin("https://host$index:5090", "nana", index.toLong())) }
        answer(ApiResult.Success(HostOperatingSystemKind.WindowsServer))

        assertEquals(6, lookup.resolve(profiles.all()))

        assertEquals(6, gateway.hostOperatingSystemLookups.size)
        assertTrue(profiles.all().all { it.hostOperatingSystem == HostOperatingSystemKind.WindowsServer })
    }

    @Test
    fun `an answer that matches what is stored is not counted as a change`() = runTest {
        // The row was filled in between the snapshot the caller read and the answer arriving (a second
        // open, or a sign-in). Storing the same value again must not report a change — the count is
        // what decides whether the screen recomposes at all.
        profiles.upsert(alpha.copy(hostOperatingSystem = HostOperatingSystemKind.Ubuntu))
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(0, lookup.resolve(listOf(alpha)))

        assertEquals(1, gateway.hostOperatingSystemLookups.size)
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    private fun answer(result: ApiResult<HostOperatingSystemKind>) {
        gateway.onHostOperatingSystem = { result }
    }
}
