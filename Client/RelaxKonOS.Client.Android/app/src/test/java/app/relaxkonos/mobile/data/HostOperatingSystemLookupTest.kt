package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.security.model.SavedLogin
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Filling in the host mark of a saved connection.
 *
 * A service can change systems, multiple accounts share an answer, and unavailable tunnels stay closed.
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

        assertEquals(1, lookup.resolve(profiles.all().map { it.serviceId }))

        assertEquals(listOf("https://alpha:5090"), gateway.hostOperatingSystemLookups)
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `the same endpoint changes from windows to ubuntu on the next open`() = runTest {
        profiles.upsert(alpha.copy(hostOperatingSystem = HostOperatingSystemKind.Windows11))
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(1, lookup.resolve(profiles.all().map { it.serviceId }))

        assertEquals(1, gateway.hostOperatingSystemLookups.size)
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `unknown is refreshed again and unchanged answers do not rewrite`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Success(HostOperatingSystemKind.Unknown))

        assertEquals(1, lookup.resolve(profiles.all().map { it.serviceId }))
        assertEquals(0, lookup.resolve(profiles.all().map { it.serviceId }))

        assertEquals(2, gateway.hostOperatingSystemLookups.size)
        assertEquals(HostOperatingSystemKind.Unknown, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a failed lookup is not an answer, so the next open can try again`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Transport("no route to host"))

        assertEquals(0, lookup.resolve(profiles.all().map { it.serviceId }))
        assertNull(profiles.all().single().hostOperatingSystem)

        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))
        assertEquals(1, lookup.resolve(profiles.all().map { it.serviceId }))
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a refused lookup is not stored either`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Problem(status = 404, code = "not-found", traceId = null))

        assertEquals(0, lookup.resolve(profiles.all().map { it.serviceId }))

        assertNull(profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a managed login is never asked, because its address exists only while its host is open`() = runTest {
        profiles.upsert(managed)
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))

        assertEquals(0, lookup.resolve(profiles.all().map { it.serviceId }))

        assertTrue(gateway.hostOperatingSystemLookups.isEmpty())
    }

    @Test
    fun `every unasked entry is asked, whatever the parallel limit is`() = runTest {
        (1..6).forEach { index -> profiles.upsert(SavedLogin("https://host$index:5090", "nana", index.toLong())) }
        answer(ApiResult.Success(HostOperatingSystemKind.WindowsServer))

        assertEquals(6, lookup.resolve(profiles.all().map { it.serviceId }))

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

        lookup.resolve(listOf(alpha.serviceId))
        assertEquals(0, lookup.resolve(listOf(alpha.serviceId)))

        assertEquals(2, gateway.hostOperatingSystemLookups.size)
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }

    private fun answer(result: ApiResult<HostOperatingSystemKind>) {
        gateway.onHostOperatingSystem = { result }
    }

    @Test
    fun `unknown can become a supported system at the same address`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Success(HostOperatingSystemKind.Unknown))
        lookup.resolve(listOf(alpha.serviceId))
        answer(ApiResult.Success(HostOperatingSystemKind.Windows10))
        lookup.resolve(listOf(alpha.serviceId))
        assertEquals(HostOperatingSystemKind.Windows10, lookup.systems.value[alpha.serviceId])
        assertEquals(HostOperatingSystemKind.Windows10, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `accounts on the same service share one request and preserve credential state`() = runTest {
        profiles.upsert(alpha.copy(hasSavedCredential = true))
        profiles.upsert(alpha.copy(identifier = "root", lastUsedEpochMillis = 200L))
        answer(ApiResult.Success(HostOperatingSystemKind.WindowsServer))
        lookup.resolve(profiles.all().map { it.serviceId })
        assertEquals(1, gateway.hostOperatingSystemLookups.size)
        assertTrue(profiles.all().all { it.hostOperatingSystem == HostOperatingSystemKind.WindowsServer })
        assertTrue(profiles.all().first { it.identifier == "nana" }.hasSavedCredential)
        assertEquals(200L, profiles.all().first().lastUsedEpochMillis)
    }

    @Test
    fun `password-only service receives a mark without recreating a login`() = runTest {
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))
        lookup.resolve(listOf(alpha.serviceId))
        assertEquals(HostOperatingSystemKind.Ubuntu, lookup.systems.value[alpha.serviceId])
        assertTrue(profiles.all().isEmpty())
    }

    @Test
    fun `failure retains the last observed mark and retries on the next open`() = runTest {
        profiles.upsert(alpha)
        answer(ApiResult.Success(HostOperatingSystemKind.Windows11))
        lookup.resolve(listOf(alpha.serviceId))
        answer(ApiResult.Transport("offline"))
        assertEquals(0, lookup.resolve(listOf(alpha.serviceId)))
        assertEquals(HostOperatingSystemKind.Windows11, lookup.systems.value[alpha.serviceId])
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))
        assertEquals(1, lookup.resolve(listOf(alpha.serviceId)))
    }

    @Test
    fun `managed active session uses its current tunnel without persisting the loopback address`() = runTest {
        profiles.upsert(managed)
        val active = SessionState.Active(managed.serviceId, "http://127.0.0.1:32001", "nana", "studio",
            emptySet(), "linux", ExecutionEligibility.Available)
        answer(ApiResult.Success(HostOperatingSystemKind.Ubuntu))
        lookup.resolve(listOf(managed.serviceId), active)
        answer(ApiResult.Success(HostOperatingSystemKind.Windows11))
        lookup.resolve(listOf(managed.serviceId), active.copy(effectiveBaseUrl = "http://127.0.0.1:32002"))
        assertEquals(listOf("http://127.0.0.1:32001", "http://127.0.0.1:32002"), gateway.hostOperatingSystemLookups)
        assertEquals(managed.serviceId, profiles.all().single().serviceId)
        assertEquals(HostOperatingSystemKind.Windows11, profiles.all().single().hostOperatingSystem)
    }

    @Test
    fun `a late answer from an older request cannot overwrite a newer system`() = runTest {
        profiles.upsert(alpha)
        val started = CompletableDeferred<Unit>()
        val finish = CompletableDeferred<Unit>()
        var calls = 0
        gateway.onHostOperatingSystem = {
            if (++calls == 1) {
                started.complete(Unit)
                finish.await()
                ApiResult.Success(HostOperatingSystemKind.Windows11)
            } else ApiResult.Success(HostOperatingSystemKind.Ubuntu)
        }
        val older = async { lookup.resolve(listOf(alpha.serviceId)) }
        started.await()
        lookup.resolve(listOf(alpha.serviceId))
        finish.complete(Unit)
        assertEquals(0, older.await())
        assertEquals(HostOperatingSystemKind.Ubuntu, lookup.systems.value[alpha.serviceId])
        assertEquals(HostOperatingSystemKind.Ubuntu, profiles.all().single().hostOperatingSystem)
    }
}
