package app.relaxkonos.mobile.ui.manage

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ManageViewModelTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val recent = RecentOperationJournal()
    private val store = ViewModelStore()
    private lateinit var model: ManageViewModel
    private val process = RemoteProcess(42, "review-process", 0.0, 10, null, 1, "2026-10-01T00:00:00Z")
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = ManageViewModel(session, SystemRepository(gateway, session), recent)
        store.put("manage", model)
        gateway.onProcesses = { _, _, _, _, _, _, _ -> ApiResult.Success(ProcessPage(listOf(process), 1, process.startTime!!)) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.PROCESSES))) }) }
        session.login(ServerConnectionIdentityRules.direct("https://process-model-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `read exception ends busy and explicit read recovers without writes`() = runTest {
        login(); runCurrent()
        gateway.onProcesses = { _, _, _, _, _, _, _ -> error("private provider detail") }
        model.startProcessObserving(); runCurrent()
        assertFalse(model.processesLoading); assertNotNull(model.processMessage)
        assertNull(model.processMessage?.debugDetail)
        gateway.onProcesses = { _, _, _, _, _, _, _ -> ApiResult.Success(ProcessPage(listOf(process), 1, process.startTime!!)) }
        model.loadProcesses(); runCurrent()
        assertEquals(listOf(process), model.processItems); assertNull(model.processMessage)
        assertTrue(gateway.killCalls.isEmpty())
    }
    @Test fun `termination exception retains unknown through refresh and never records success`() = runTest {
        login(); runCurrent(); model.startProcessObserving(); runCurrent()
        gateway.onKill = { _, _, _, _ -> error("private provider detail") }
        model.requestKill(process); model.confirmKill(); model.confirmKill(); runCurrent()
        assertFalse(model.processesLoading)
        assertEquals(R.string.manage_processes_kill_unknown, model.killMessage?.resId)
        model.loadProcesses(); runCurrent()
        assertEquals(R.string.manage_processes_kill_unknown, model.killMessage?.resId)
        assertEquals(listOf(process.pid to process.startTime!!), gateway.killCalls)
        assertTrue(recent.entries.value.isEmpty())
    }
    @Test fun `leaving during termination does not restart reads or replay the write`() = runTest {
        login(); runCurrent(); model.startProcessObserving(); runCurrent()
        val receipt = CompletableDeferred<ApiResult<ProcessKillResult>>()
        var reads = 0
        gateway.onProcesses = { _, _, _, _, _, _, _ -> reads++; ApiResult.Success(ProcessPage(listOf(process), 1, process.startTime!!)) }
        gateway.onKill = { _, _, _, _ -> receipt.await() }
        model.requestKill(process); model.confirmKill(); runCurrent()
        model.stopProcessObserving(); model.loadProcesses(); runCurrent()
        assertTrue(model.processesLoading)
        receipt.complete(ApiResult.Success(ProcessKillResult(true, false, "", null))); runCurrent()
        assertFalse(model.processesLoading); assertEquals(0, reads)
        assertEquals(1, gateway.killCalls.size); assertEquals(1, recent.entries.value.size)
    }
    @Test fun `new login discards old confirmation and cannot send it`() = runTest {
        login(); runCurrent(); model.startProcessObserving(); runCurrent()
        model.requestKill(process); assertNotNull(model.killTarget)
        login(); runCurrent(); model.confirmKill(); runCurrent()
        assertNull(model.killTarget); assertTrue(model.processItems.isEmpty())
        assertTrue(gateway.killCalls.isEmpty()); assertTrue(recent.entries.value.isEmpty())
    }
    @Test fun `refresh withdraws confirmation when the original process disappears or pid is reused`() = runTest {
        login(); runCurrent(); model.startProcessObserving(); runCurrent()
        for (replacement in listOf(emptyList(), listOf(process.copy(startTime = "2026-10-01T00:00:01Z")))) {
            gateway.onProcesses = { _, _, _, _, _, _, _ -> ApiResult.Success(ProcessPage(listOf(process), 1, process.startTime!!)) }
            model.loadProcesses(); runCurrent(); model.requestKill(process)
            assertNotNull(model.killTarget)
            gateway.onProcesses = { _, _, _, _, _, _, _ -> ApiResult.Success(ProcessPage(replacement, replacement.size, process.startTime!!)) }
            model.loadProcesses(); runCurrent()
            assertNull("Obsolete process confirmation must be withdrawn", model.killTarget)
            model.confirmKill(); runCurrent()
        }
        assertTrue(gateway.killCalls.isEmpty())
    }
    @Test fun `metrics refresh for the same instance preserves the original confirmation`() = runTest {
        login(); runCurrent(); model.startProcessObserving(); runCurrent()
        model.requestKill(process)
        gateway.onProcesses = { _, _, _, _, _, _, _ -> ApiResult.Success(ProcessPage(listOf(process.copy(cpuPercent = 10.0)), 1, process.startTime!!)) }
        model.loadProcesses(); runCurrent()
        assertEquals(process, model.killTarget)
        gateway.onKill = { _, _, _, _ -> ApiResult.Problem(403, "denied", null) }
        model.confirmKill(); runCurrent()
        assertEquals(listOf(process.pid to process.startTime!!), gateway.killCalls)
        assertTrue(recent.entries.value.isEmpty())
    }
}
