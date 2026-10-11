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
}
