package app.relaxkonos.mobile.ui.manage.docker

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.*

@OptIn(ExperimentalCoroutinesApi::class)
class DockerResourceViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val journal = DockerResourceJournal(Storage())
    private val gate = DockerMutationGate(DockerControlJournal(Storage()), journal, DockerComposeJournal(Storage())) { ApiResult.Success(Unit) }
    private val repository = DockerResourceRepository(gateway, session, journal, gate)
    private val facts = DockerResourceFacts(DockerStatus(true, "", "28", "linux", "amd64"), emptyList(), emptyList(), emptyList(), emptyList())
    private val change = DockerResourceChange(DockerResourceAction.CreateVolume, value = "review-data", driver = "local")
    private val store = ViewModelStore()
    private lateinit var model: DockerResourceViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = DockerResourceViewModel(session, repository, gate)
        store.put("resources", model)
        gateway.onDockerStatus = { ApiResult.Success(facts.status) }
        gateway.onDockerContainers = { ApiResult.Success(emptyList()) }
        gateway.onDockerImages = { ApiResult.Success(emptyList()) }
        gateway.onDockerNetworks = { ApiResult.Success(emptyList()) }
        gateway.onDockerVolumes = { ApiResult.Success(emptyList()) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.DOCKER), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://docker-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `cancelled refresh removes facts until explicit recovery`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts)
        gateway.onDockerStatus = { throw CancellationException("private read") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertEquals("docker.control.unverified", model.state.problem)
        gateway.onDockerStatus = { ApiResult.Success(facts.status) }
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
    }
    @Test fun `cancelled creation keeps pending marker without replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var sends = 0
        gateway.onDockerResourceChange = { sends++; throw CancellationException("private write") }
        model.change(facts, null, change); advanceUntilIdle()
        assertNull(model.state.facts); assertEquals(1, model.state.pending.size)
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertEquals(1, model.state.pending.size)
        assertEquals(1, sends); assertNotNull(model.state.blocked)
    }
    @Test fun `adoption failure does not reread facts`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val marker = journal.begin(requireNotNull(model.state.owner), change)
        var reads = 0
        gateway.onDockerStatus = { reads++; if (reads == 1) ApiResult.Transport(null) else ApiResult.Success(facts.status) }
        model.accept(marker); advanceUntilIdle()
        assertNull(model.state.facts); assertEquals(listOf(marker), model.state.pending)
        assertEquals(1, reads); assertEquals("docker.control.unverified", model.state.problem)
    }
}
