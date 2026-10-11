package app.relaxkonos.mobile.ui.manage.guardian

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.GuardianRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class GuardianViewModelTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val store = ViewModelStore()
    private lateinit var model: GuardianViewModel
    private lateinit var owner: SessionState.Active
    private val definition = GuardianDefinition("job", "Job", "/bin/job", emptyList(), "/tmp", false, 15, 3, null, "review", "uid:1000")
    private var actions = 0
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = GuardianViewModel(session, GuardianRepository(gateway, session))
        store.put("guardian", model)
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        gateway.onGuardianStatus = { ApiResult.Success(GuardianStatus(true, true, "", "test")) }
        gateway.onGuardianWorkloads = { ApiResult.Success(emptyList()) }
        gateway.onGuardianDefinition = { ApiResult.Success(GuardianDefinitionResult(true, "", definition)) }
        gateway.onGuardianLogs = { ApiResult.Success(emptyList()) }
        gateway.onGuardianAction = { _, _ -> actions++; throw CancellationException("private action detail") }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        session.login(ServerConnectionIdentityRules.direct("https://guardian-review.invalid"), "review", "test".toCharArray()) {}
        owner = session.state.value as SessionState.Active
    }
    @Test fun `cancelled read revokes facts and explicit refresh restores them`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        assertFalse(model.state.value.stale)
        gateway.onGuardianStatus = { throw CancellationException("private read detail") }
        model.load(owner); advanceUntilIdle()
        assertNull(model.state.value.status); assertTrue(model.state.value.stale)
        assertTrue(model.state.value.error); assertFalse(model.state.value.loading)
        model.act("job", "restart"); advanceUntilIdle(); assertEquals(0, actions)
        gateway.onGuardianStatus = { ApiResult.Success(GuardianStatus(true, true, "", "test")) }
        model.load(owner); advanceUntilIdle(); assertFalse(model.state.value.stale)
    }
    @Test fun `cancelled mutation remains unknown and cannot repeat before reconciliation`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        model.act("job", "restart"); advanceUntilIdle()
        assertTrue(model.state.value.unknown); assertTrue(model.state.value.error); assertFalse(model.state.value.loading)
        model.act("job", "restart"); advanceUntilIdle(); assertEquals(1, actions)
        model.load(owner); advanceUntilIdle(); assertFalse(model.state.value.unknown)
    }
    @Test fun `cancelled editor read retains draft and revokes old facts`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        model.beginEdit(definition, false)
        val draft = model.editing
        gateway.onGuardianDefinition = { throw CancellationException("private definition detail") }
        model.readEditor(); advanceUntilIdle()
        assertSame(draft, model.editing); assertTrue(model.state.value.stale); assertTrue(model.state.value.error)
        assertFalse(model.state.value.loading)
    }
    @Test fun `stale editor blocks repository save and clears supplied approval`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        model.beginEdit(definition, false)
        val draft = requireNotNull(model.editing)
        gateway.onGuardianDefinition = { throw CancellationException("private definition detail") }
        model.readEditor(); advanceUntilIdle()
        var reads = 0
        gateway.onGuardianDefinition = { reads++; ApiResult.Success(GuardianDefinitionResult(true, "", definition)) }
        val password = "test approval".toCharArray()
        model.save(draft, definition, GuardianApproval("root", password)); advanceUntilIdle()
        assertEquals(0, reads); assertSame(draft, model.editing)
        assertTrue(password.all { it == '\u0000' })
    }
    @Test fun `failed editor read revokes baseline until successful reread`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        model.beginEdit(definition, false)
        gateway.onGuardianDefinition = { ApiResult.Transport(null) }
        model.readEditor(); advanceUntilIdle()
        assertTrue(model.state.value.stale)
        gateway.onGuardianDefinition = { ApiResult.Success(GuardianDefinitionResult(true, "", definition)) }
        model.readEditor(); advanceUntilIdle()
        assertFalse(model.state.value.stale); assertFalse(model.state.value.error)
    }
    @Test fun `switching selection does not let cancelled old read revoke new facts`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        val pending = CompletableDeferred<ApiResult<GuardianDefinitionResult>>()
        var reads = 0
        gateway.onGuardianDefinition = { id -> if (++reads == 1) pending.await()
            else ApiResult.Success(GuardianDefinitionResult(true, "", definition.copy(id = id))) }
        model.select("job"); runCurrent()
        model.select("other"); model.select("job"); advanceUntilIdle()
        assertEquals("job", model.state.value.definition?.id)
        assertFalse(model.state.value.stale); assertFalse(model.state.value.error)
    }
    @Test fun `failed detail read revokes mutation permission and retains problem code`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        gateway.onGuardianDefinition = { ApiResult.Problem(503, "guardian.agent_unavailable", null) }
        model.select("job"); advanceUntilIdle()
        assertTrue(model.state.value.stale)
        assertEquals("guardian.agent_unavailable", model.state.value.problemCode)
        model.act("job", "restart"); advanceUntilIdle(); assertEquals(0, actions)
    }
    @Test fun `failed log read revokes mutation permission and retains problem code`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        gateway.onGuardianLogs = { ApiResult.Problem(503, "guardian.agent_unavailable", null) }
        model.select("job"); advanceUntilIdle()
        assertTrue(model.state.value.stale)
        assertEquals("guardian.agent_unavailable", model.state.value.problemCode)
        model.act("job", "restart"); advanceUntilIdle(); assertEquals(0, actions)
    }
    @Test fun `background polling preserves failed detail gate until explicit refresh`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        gateway.onGuardianDefinition = { ApiResult.Problem(503, "guardian.agent_unavailable", null) }
        model.select("job"); advanceUntilIdle()
        model.load(owner, reconcile = false); advanceUntilIdle()
        assertTrue(model.state.value.stale); assertTrue(model.state.value.error)
        assertEquals("guardian.agent_unavailable", model.state.value.problemCode)
        model.act("job", "restart"); advanceUntilIdle(); assertEquals(0, actions)
        model.load(owner); advanceUntilIdle()
        assertFalse(model.state.value.stale); assertFalse(model.state.value.error)
    }
    @Test fun `background polling preserves rejected action feedback until explicit refresh`() = runTest {
        login(); runCurrent(); model.load(owner); advanceUntilIdle()
        gateway.onGuardianAction = { _, _ -> actions++; ApiResult.Problem(403, "guardian.agent_permission_denied", null) }
        model.act("job", "restart"); advanceUntilIdle()
        model.load(owner, reconcile = false); advanceUntilIdle()
        assertTrue(model.state.value.error)
        assertEquals("guardian.agent_permission_denied", model.state.value.problemCode)
        assertEquals(1, actions)
        model.load(owner); advanceUntilIdle()
        assertFalse(model.state.value.error); assertNull(model.state.value.problemCode)
    }
    @Test fun `observation initializes facts before starting background polling`() = runTest {
        login(); runCurrent()
        gateway.onGuardianStatus = { delay(100); ApiResult.Success(GuardianStatus(true, true, "", "test")) }
        try {
            model.observe(owner, true); runCurrent()
            model.load(owner)
            advanceTimeBy(101); runCurrent()
            assertFalse(model.state.value.loading); assertFalse(model.state.value.stale)
        } finally { model.stopObserving(); runCurrent() }
    }
}
