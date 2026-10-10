package app.relaxkonos.mobile.ui.home

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.After
import org.junit.Before
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class HomeViewModelTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val store = ViewModelStore()
    private lateinit var model: HomeViewModel
    private val sample = PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT)
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = HomeViewModel(session, SystemRepository(gateway, session), RecentOperationJournal()) { null }
        store.put("home", model)
        gateway.onPerformance = { _, _ -> ApiResult.Success(sample) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.METRICS)))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://home-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `same address new login clears the old sample and feedback`() = runTest {
        login(); runCurrent(); model.refresh(); runCurrent(); assertNotNull(model.snapshot)
        login(); runCurrent()
        assertNull(model.snapshot); assertNull(model.message); assertFalse(model.loading)
        model.refresh(); runCurrent(); assertEquals(sample, model.snapshot)
    }
    @Test fun `cancelled read ends busy and a new explicit refresh can recover`() = runTest {
        login(); runCurrent()
        gateway.onPerformance = { _, _ -> throw CancellationException("cancelled read") }
        model.refresh(); runCurrent()
        assertFalse(model.loading); assertNull(model.message)
        gateway.onPerformance = { _, _ -> ApiResult.Success(sample) }
        model.refresh(); runCurrent(); assertEquals(sample, model.snapshot)
    }
    @Test fun `failed refresh does not keep presenting the old sample as current`() = runTest {
        login(); runCurrent(); model.refresh(); runCurrent(); assertNotNull(model.snapshot)
        gateway.onPerformance = { _, _ -> error("private metrics detail") }
        model.refresh(); runCurrent()
        assertNull(model.snapshot); assertNotNull(model.message); assertFalse(model.loading)
        assertNull(model.message?.debugDetail)
    }
    @Test fun `late cancelled response cannot replace a new login sample or busy state`() = runTest {
        login(); runCurrent()
        val reply = CompletableDeferred<ApiResult<PerformanceSnapshot>>()
        gateway.onPerformance = { _, _ -> withContext(NonCancellable) { reply.await() } }
        val nextSample = sample.copy(sequence = sample.sequence + 1)
        try {
            model.refresh(); runCurrent(); assertTrue(model.loading)
            login(); runCurrent()
            assertFalse(model.loading); assertNull(model.snapshot)
            gateway.onPerformance = { _, _ -> ApiResult.Success(nextSample) }
            model.refresh(); runCurrent(); assertEquals(nextSample, model.snapshot)
        } finally { reply.complete(ApiResult.Success(sample)); runCurrent() }
        assertEquals(nextSample, model.snapshot); assertFalse(model.loading); assertNull(model.message)
    }
}
