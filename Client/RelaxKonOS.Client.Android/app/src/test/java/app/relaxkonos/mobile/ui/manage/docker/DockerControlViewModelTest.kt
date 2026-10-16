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
    private val gate = DockerMutationGate(journal, resourceJournal, DockerComposeJournal(Storage())) { ApiResult.Success(Unit) }
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
    private val task = InstallationOperation("11111111-1111-1111-1111-111111111111", InstallationService.Docker, InstallationKind.Install,
        InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
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
    @Test fun `old cancel cannot target a replacement installation while fresh confirmation can`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        val next = task.copy(operationId = "22222222-2222-2222-2222-222222222222")
        val tasks = listOf(task, next).associateBy { it.operationId }
        val cancellations = mutableListOf<String>()
        gateway.onInstallation = { ApiResult.Success(requireNotNull(tasks[it])) }
        gateway.onCancelInstallation = { id, _ -> cancellations += id; ApiResult.Success(requireNotNull(tasks[id]).copy(state = InstallationState.Cancelled)) }
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        model.recoverInstall(next.operationId, false); advanceUntilIdle()
        model.cancelInstall(owner, task.operationId); advanceUntilIdle()
        assertTrue(cancellations.isEmpty()); assertEquals(next.operationId, model.state.installation?.operationId)
        model.cancelInstall(owner, next.operationId); advanceUntilIdle()
        assertEquals(listOf(next.operationId), cancellations)
    }
    @Test fun `terminal unverified and noncancellable tasks reject old cancel without changing facts`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        var current = task.copy(state = InstallationState.Succeeded)
        var writes = 0
        gateway.onInstallation = { ApiResult.Success(current) }
        gateway.onCancelInstallation = { _, _ -> writes++; ApiResult.Success(current) }
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        val terminal = model.state
        model.cancelInstall(owner, task.operationId); advanceUntilIdle()
        assertEquals(terminal, model.state)
        current = task.copy(cancellable = false)
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        model.cancelInstall(owner, task.operationId); advanceUntilIdle()
        current = task
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        gateway.onInstallation = { ApiResult.Transport(null) }
        model.pollInstall(); advanceUntilIdle()
        val unverified = model.state
        model.cancelInstall(owner, task.operationId); advanceUntilIdle()
        assertEquals(unverified, model.state); assertFalse(model.state.installationVerified); assertEquals(0, writes)
    }
    @Test fun `same address new login cannot reuse old cancel owner`() = runTest {
        login(); runCurrent()
        val previousOwner = requireNotNull(model.state.owner)
        var writes = 0
        gateway.onInstallation = { ApiResult.Success(task) }
        gateway.onCancelInstallation = { _, _ -> writes++; ApiResult.Success(task.copy(state = InstallationState.Cancelled)) }
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        login(); runCurrent()
        model.recoverInstall(task.operationId, false); advanceUntilIdle()
        model.cancelInstall(previousOwner, task.operationId); advanceUntilIdle()
        assertEquals(0, writes)
        model.cancelInstall(requireNotNull(model.state.owner), task.operationId); advanceUntilIdle()
        assertEquals(1, writes)
    }
    @Test fun `old installation timer cannot query a newly selected task`() = runTest {
        login(); runCurrent()
        val next = task.copy(operationId = "22222222-2222-2222-2222-222222222222")
        val reads = mutableListOf<String>()
        gateway.onInstallation = { id -> reads += id; ApiResult.Success(if (id == task.operationId) task else next) }
        model.recoverInstall(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }
        runCurrent()
        model.recoverInstall(next.operationId, false); runCurrent()
        advanceTimeBy(1_500); runCurrent(); timer.join()
        assertEquals(listOf(task.operationId, next.operationId), reads)
    }
    @Test fun `installation timer rechecks foreground after delay`() = runTest {
        login(); runCurrent()
        var foreground = true; var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        model.recoverInstall(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { foreground } }; runCurrent()
        foreground = false
        advanceTimeBy(1_500); runCurrent(); timer.join()
        assertEquals(1, reads); assertFalse(model.state.busy)
    }
    @Test fun `identical running installation replies keep observing until terminal`() = runTest {
        login(); runCurrent()
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(if (reads < 4) task else task.copy(state = InstallationState.Succeeded)) }
        model.recoverInstall(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }; runCurrent()
        advanceTimeBy(4_500); runCurrent(); timer.join()
        assertEquals(4, reads); assertEquals(InstallationState.Succeeded, model.state.installation?.state)
    }
    @Test fun `manual reread of same installation invalidates old observation deadline`() = runTest {
        login(); runCurrent()
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        model.recoverInstall(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }; runCurrent()
        advanceTimeBy(1_000)
        model.recoverInstall(task.operationId, false); runCurrent()
        advanceTimeBy(500); runCurrent()
        assertEquals(2, reads)
        timer.cancelAndJoin()
    }
}
