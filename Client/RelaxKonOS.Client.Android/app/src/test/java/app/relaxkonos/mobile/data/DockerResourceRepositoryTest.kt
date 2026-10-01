package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test
class DockerResourceRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = DockerResourceJournal(storage); private val control = DockerControlJournal(Storage())
    private var installation: ApiResult<Unit> = ApiResult.Success(Unit)
    private val gate = DockerMutationGate(control, journal) { installation }
    private val repository = DockerResourceRepository(gateway, session, journal, gate)
    private val status = DockerStatus(true, "", "28", "linux", "amd64")
    private val detail = DockerContainerDetails("abc123ffff", "web", "nginx", "now", "running", "Up", "serve", "", "no", emptyList(), emptyList(), listOf("bridge"), emptyList(), emptyMap())
    private val network = DockerNetworkDetails("def456", "custom", "bridge", "local", emptyList(), emptyMap())
    private val volume = DockerVolumeDetails("data", "local", "/data", emptyList(), emptyMap())
    private val facts = DockerResourceFacts(status, listOf(DockerContainer("abc123", "web", "nginx", "running", "Up")),
        listOf(DockerImage("sha256:" + "a".repeat(64), "nginx", "alpine", "10MB", "now")), listOf(DockerNetwork("def456", "custom", "bridge", "local")), listOf(DockerVolume("data", "local", "/data")))
    private val target = DockerResourceTarget(DockerResourceKind.Containers, "abc123", container = detail)
    private suspend fun signIn(manage: Boolean = true, account: String = "alice"): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account,
            server = it.server.copy(capabilities = setOf(ServerCapabilities.DOCKER), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        gateway.onDockerStatus = { ApiResult.Success(status) }; gateway.onDockerContainers = { ApiResult.Success(facts.containers!!) }
        gateway.onDockerImages = { ApiResult.Success(facts.images!!) }; gateway.onDockerNetworks = { ApiResult.Success(facts.networks!!) }; gateway.onDockerVolumes = { ApiResult.Success(facts.volumes!!) }
        gateway.onDockerContainerDetails = { ApiResult.Success(detail) }; gateway.onDockerNetworkDetails = { ApiResult.Success(network) }; gateway.onDockerVolumeDetails = { ApiResult.Success(volume) }
        return session.state.value as SessionState.Active
    }
    @Test fun `unknown creation stores no body or secret and requires explicit adoption without replay`() = runTest {
        val owner = signIn(); var calls = 0
        val change = DockerResourceChange(DockerResourceAction.CreateContainer, container = DockerContainerCreate("PrivateName", "nginx", listOf("PrivateCommand"), environment = listOf("TOKEN=PrivateSecret")))
        gateway.onDockerResourceChange = { calls++; ApiResult.Transport(null) }
        assertTrue(repository.change(owner, facts, null, change) is ApiResult.Transport)
        listOf("PrivateName", "PrivateCommand", "PrivateSecret").forEach { assertFalse(storage.bytes!!.decodeToString().contains(it)) }
        val marker = DockerResourceJournal(storage).pending(owner).single(); repository.facts(owner)
        assertTrue(repository.change(owner, facts, null, change) is ApiResult.Problem); assertEquals(1, calls)
        assertTrue(repository.acceptFacts(owner, marker) is ApiResult.Success); assertTrue(repository.pending(owner).isEmpty()); assertEquals(1, calls)
    }
    @Test fun `all collection failures stay unknown and unavailable engine cannot adopt resource result`() = runTest {
        val owner = signIn(); val marker = journal.begin(owner, DockerResourceChange(DockerResourceAction.PullImage, value = "nginx"))
        gateway.onDockerVolumes = { ApiResult.Transport(null) }; assertTrue(repository.facts(owner) is ApiResult.Transport)
        gateway.onDockerStatus = { ApiResult.Success(status.copy(available = false)) }
        val unavailable = (repository.facts(owner) as ApiResult.Success).value
        assertNull(unavailable.volumes); assertNull(unavailable.containers)
        repository.acceptFacts(owner, marker); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `changed details refuse before writing even when summary lists are unchanged`() = runTest {
        val owner = signIn(); gateway.onDockerContainerDetails = { ApiResult.Success(detail.copy(labels = mapOf("com.docker.compose.project" to "stack"))) }
        val result = repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.DeleteContainer, target.id))
        assertEquals("docker.resources.facts_changed", (result as ApiResult.Problem).code); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `relative container and image ages do not block an unchanged creation`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onDockerContainers = { ApiResult.Success(facts.containers!!.map { it.copy(status = "Up 3 minutes") }) }
        gateway.onDockerImages = { ApiResult.Success(facts.images!!.map { it.copy(createdSince = "2 hours ago") }) }
        gateway.onDockerResourceChange = { calls++; ApiResult.Success(DockerOperation(true, "", emptyList(), false)) }
        val change = DockerResourceChange(DockerResourceAction.CreateVolume, value = "test-data", driver = "local")
        assertTrue(repository.change(owner, facts, null, change) is ApiResult.Success)
        assertEquals(1, calls); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `real container state changes still block creation before writing`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onDockerContainers = { ApiResult.Success(facts.containers!!.map { it.copy(state = "exited", status = "Exited (0) 1 minute ago") }) }
        gateway.onDockerResourceChange = { calls++; ApiResult.Success(DockerOperation(true, "", emptyList(), false)) }
        val result = repository.change(owner, facts, null, DockerResourceChange(DockerResourceAction.CreateVolume, value = "test-data", driver = "local"))
        assertEquals("docker.resources.facts_changed", (result as ApiResult.Problem).code)
        assertEquals(0, calls); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `managed resource and occupied or builtin network and stopped volume references refuse mutations`() = runTest {
        val owner = signIn()
        val managed = target.copy(container = detail.copy(labels = mapOf("relaxkonos.owner" to "application-deployment")))
        assertTrue(runCatching { repository.change(owner, facts, managed, DockerResourceChange(DockerResourceAction.Stop, target.id)) }.isFailure)
        for (n in listOf(network.copy(containers = listOf("stopped")), network.copy(name = "bridge"))) {
            assertTrue(runCatching { repository.change(owner, facts, DockerResourceTarget(DockerResourceKind.Networks, n.id, network = n), DockerResourceChange(DockerResourceAction.DeleteNetwork, n.id)) }.isFailure)
        }
        assertTrue(runCatching { repository.change(owner, facts, DockerResourceTarget(DockerResourceKind.Volumes, "data", volume = volume.copy(usedBy = listOf("stopped"))), DockerResourceChange(DockerResourceAction.DeleteVolume, "data")) }.isFailure)
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `control unknown and active or unverified installation block standalone and Compose writes`() = runTest {
        val owner = signIn(); val marker = control.begin(owner, DockerControlChange(DockerControlKind.EngineRestart))
        val change = DockerResourceChange(DockerResourceAction.Start, target.id)
        assertTrue(repository.change(owner, facts, target, change) is ApiResult.Problem)
        val index = OperationIndex(object : OperationIndexStorage { override fun read(): ByteArray? = null; override fun write(bytes: ByteArray) = Unit })
        assertTrue(DockerRepository(gateway, session, index, gate).stackAction(owner, "stack", "start", true) is ApiResult.Problem)
        control.complete(marker); installation = ApiResult.Transport(null)
        assertTrue(repository.change(owner, facts, target, change) is ApiResult.Transport); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `resource unknown blocks engine writes through same gate`() = runTest {
        val owner = signIn(); journal.begin(owner, DockerResourceChange(DockerResourceAction.DeleteContainer, target.id))
        assertTrue(DockerControlRepository(gateway, session, control, gate).change(owner, DockerControlFacts(status, emptyList()), DockerControlChange(DockerControlKind.EngineStop)) is ApiResult.Problem)
    }
    @Test fun `successful mutation rereads actual facts before clearing original marker`() = runTest {
        val owner = signIn(); gateway.onDockerResourceChange = { change ->
            assertEquals(DockerResourceAction.RenameContainer, change.action); assertEquals("new-name", change.value)
            gateway.onDockerContainers = { ApiResult.Success(facts.containers!!.map { it.copy(names = "new-name") }) }
            ApiResult.Success(DockerOperation(true, "", listOf("done"), false))
        }
        assertTrue(repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.RenameContainer, target.id, "new-name")) is ApiResult.Success)
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `success with lost postread or explicit failed operation remains uncertain`() = runTest {
        val owner = signIn(); gateway.onDockerResourceChange = { gateway.onDockerVolumes = { ApiResult.Transport(null) }; ApiResult.Success(DockerOperation(true, "", emptyList(), false)) }
        repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)); assertEquals(1, repository.pending(owner).size)
        gateway.onDockerVolumes = { ApiResult.Success(facts.volumes!!) }; repository.acceptFacts(owner, repository.pending(owner).single())
        gateway.onDockerResourceChange = { ApiResult.Success(DockerOperation(false, "docker.operation_failed", emptyList(), false)) }
        repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `401 retry rereads detail ownership before resending rejected write`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onDockerResourceChange = { calls++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        gateway.onRefresh = { _, _ -> gateway.onDockerContainerDetails = { ApiResult.Success(detail.copy(state = "exited")) }; ApiResult.Success(AuthTokens("fresh", "refresh-2", null, null)) }
        assertEquals("docker.resources.facts_changed", (repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)) as ApiResult.Problem).code)
        assertEquals(1, calls); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `read identities must match selected resource including full container id prefix`() = runTest {
        val owner = signIn(); assertTrue(repository.target(owner, DockerResourceKind.Containers, target.id) is ApiResult.Success)
        gateway.onDockerContainerDetails = { ApiResult.Success(detail.copy(id = "fffffff")) }; assertTrue(repository.target(owner, DockerResourceKind.Containers, target.id) is ApiResult.Transport)
        gateway.onDockerContainerStats = { ApiResult.Success(DockerContainerStats("fffffff", "0", "0", "0", "0")) }; assertTrue(repository.stats(owner, target.id) is ApiResult.Transport)
        gateway.onDockerVolumeDetails = { ApiResult.Success(volume.copy(name = "other")) }; assertTrue(repository.target(owner, DockerResourceKind.Volumes, "data") is ApiResult.Transport)
    }
    @Test fun `observer switched session and corrupt journal cannot write or erase another owner marker`() = runTest {
        val observer = signIn(false); assertTrue(runCatching { repository.change(observer, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)) }.isFailure)
        val owner = signIn(); val marker = journal.begin(owner, DockerResourceChange(DockerResourceAction.Start, target.id)); val bob = signIn(account = "bob")
        assertTrue(repository.pending(bob).isEmpty()); assertTrue(runCatching { repository.acceptFacts(bob, marker) }.isFailure)
        assertTrue(runCatching { repository.facts(owner) }.exceptionOrNull() is CancellationException)
        storage.bytes = byteArrayOf(1, 2); assertTrue(runCatching { repository.change(bob, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)) }.isFailure)
    }
    @Test fun `cancelled write waiting for shared gate starts no local marker or host request`() = runTest {
        val owner = signIn(); gate.mutex.lock()
        val job = launch(start = CoroutineStart.UNDISPATCHED) { repository.change(owner, facts, target, DockerResourceChange(DockerResourceAction.Start, target.id)) }
        job.cancelAndJoin(); gate.mutex.unlock(); assertTrue(repository.pending(owner).isEmpty())
    }
}
