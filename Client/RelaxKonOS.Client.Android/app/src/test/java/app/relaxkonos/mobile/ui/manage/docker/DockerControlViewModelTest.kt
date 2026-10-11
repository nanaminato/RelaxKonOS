package app.relaxkonos.mobile.ui.manage.docker

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.*

@OptIn(ExperimentalCoroutinesApi::class)
class DockerControlViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val journal = DockerControlJournal(Storage())
    private val resourceJournal = DockerResourceJournal(Storage())
    private val gate = DockerMutationGate(journal, resourceJournal) { ApiResult.Success(Unit) }
        private val repository = DockerControlRepository(gateway, session, journal, gate)
    private val resources = DockerResourceRepository(gateway, session, resourceJournal, gate)
    private val index = OperationIndex(object : OperationIndexStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val installations = InstallationRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage())),
        index, InstallationRequestJournal(Storage()))
    private val facts = DockerControlFacts(DockerStatus(true, "", "28", "linux", "amd64"), emptyList())
    private val change = DockerControlChange(DockerControlKind.EngineRestart)
    private val store = ViewModelStore()
    private lateinit var model: DockerControlViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = DockerControlViewModel(session, repository, resources, installations, index, gate, ElevationAnswerProvider.Declines)
        store.put("resources", model)
        gateway.onDockerStatus = { ApiResult.Success(facts.status) }
        gateway.onDockerMirrors = { ApiResult.Success(emptyList()) }
        gateway.onActiveInstallation = { ApiResult.Success(null) }
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
    @Test fun `cancelled engine restart keeps pending marker without replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; throw CancellationException("private write") }
        model.change(facts, change); advanceUntilIdle()
        assertNull(model.state.facts); assertEquals(1, model.state.pending.size)
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertEquals(1, model.state.pending.size)
        assertEquals(1, sends)
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
