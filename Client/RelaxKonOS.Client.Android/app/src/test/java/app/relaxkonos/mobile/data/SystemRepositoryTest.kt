package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.core.net.PerformanceWireTest
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class SystemRepositoryTest {
    @Test fun `all metric reads return safe provider failures while cancellation still propagates`() = runTest {
        val owner = login()
        gateway.onPerformance = { _, _ -> error("private metrics provider detail") }
        gateway.onPerformanceInfo = { _, _ -> error("private metrics provider detail") }
        gateway.onPerformanceHistory = { _, _ -> error("private metrics provider detail") }
        gateway.onNetworkAddresses = { _, _ -> error("private metrics provider detail") }
        assertEquals(ApiResult.Transport(null), repository.performance())
        assertEquals(ApiResult.Transport(null), repository.performance(owner))
        assertEquals(ApiResult.Transport(null), repository.performanceInfo(owner))
        assertEquals(ApiResult.Transport(null), repository.performanceHistory(owner))
        assertEquals(ApiResult.Transport(null), repository.networkAddresses(owner))
        gateway.onPerformance = { _, _ -> throw CancellationException("cancelled") }
        assertTrue(runCatching { repository.performance(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `unexpected process read exception returns safe transport failure`() = runTest {
        val owner = login()
        gateway.onProcesses = { _, _, _, _, _, _, _ -> error("private process provider detail") }
        assertEquals(ApiResult.Transport(null), repository.processes(owner, 1, 50, null, ProcessSort.Cpu, true))
    }
    @Test fun `unexpected process termination exception is unknown and is not replayed`() = runTest {
        val owner = login()
        gateway.onKill = { _, _, _, _ -> error("private process provider detail") }
        assertEquals(ApiResult.Transport(null), repository.killProcess(owner, 42, time))
        assertEquals(listOf(42 to time), gateway.killCalls)
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = SystemRepository(gateway, session)
    private val time = "2026-10-01T00:00:00.1234567Z"
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host"), "nana", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `failed receipt and unknown transport never replay a process operation`() = runTest {
        val owner = login(); gateway.onKill = { _, _, _, _ -> ApiResult.Success(ProcessKillResult(false, true, "process.permission_denied", null)) }
        assertFalse((repository.killProcess(owner, 42, time) as ApiResult.Success).value.success)
        gateway.onKill = { _, _, _, _ -> ApiResult.Transport("timeout") }
        assertTrue(repository.killProcess(owner, 42, time) is ApiResult.Transport)
        assertEquals(listOf(42 to time, 42 to time), gateway.killCalls)
    }
    @Test fun `old login cannot send and owner switches cannot publish a late successful receipt`() = runTest {
        val owner = login(); login()
        assertTrue(runCatching { repository.killProcess(owner, 42, time) }.exceptionOrNull() is CancellationException)
        assertTrue(gateway.killCalls.isEmpty())
        val current = session.state.value as SessionState.Active
        gateway.onKill = { _, _, _, _ -> login(); ApiResult.Success(ProcessKillResult(true, false, "", null)) }
        assertTrue(runCatching { repository.killProcess(current, 42, time) }.exceptionOrNull() is CancellationException)
        assertEquals(1, gateway.killCalls.size)
    }
    @Test fun `performance reads reject an old owner before sending and late data after a switch`() = runTest {
        val owner = login(); login(); var calls = 0
        gateway.onPerformance = { _, _ -> calls++; ApiResult.Success(PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT)) }
        assertTrue(runCatching { repository.performance(owner) }.exceptionOrNull() is CancellationException)
        assertEquals(0, calls)
        val current = session.state.value as SessionState.Active
        gateway.onPerformanceInfo = { _, _ -> calls++; login(); ApiResult.Success(PerformanceWire.info(PerformanceWireTest.INFO)) }
        assertTrue(runCatching { repository.performanceInfo(current) }.exceptionOrNull() is CancellationException)
        assertEquals(1, calls)
    }
    @Test fun `process reads preserve query choice and reject late facts from another login`() = runTest {
        val owner = login(); var calls = 0
        gateway.onProcesses = { _, _, page, size, filter, sort, descending ->
            calls++; assertEquals(2, page); assertEquals(50, size); assertEquals("user", filter)
            assertEquals(ProcessSort.Memory, sort); assertFalse(descending)
            login(); ApiResult.Success(ProcessPage(emptyList(), 0, time))
        }
        assertTrue(runCatching { repository.processes(owner, 2, 50, "user", ProcessSort.Memory, false) }.exceptionOrNull() is CancellationException)
        assertEquals(1, calls)
    }

}
