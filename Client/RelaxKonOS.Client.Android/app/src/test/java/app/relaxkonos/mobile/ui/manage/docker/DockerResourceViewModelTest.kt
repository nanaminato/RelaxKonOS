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
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
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
    private suspend fun TestScope.review(): ResourceConfirmation {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        return requireNotNull(model.confirmation)
    }
    @Test fun `same facts after relogin cannot reuse old resource confirmation`() = runTest {
        val old = review(); var sends = 0
        gateway.onDockerResourceChange = { sends++; ApiResult.Transport(null) }
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.change(old); advanceUntilIdle(); assertEquals(0, sends); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `old close cannot dismiss reopened resource review`() = runTest {
        val old = review()
        val close = { model.dismissConfirmation(old) }
        close()
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        val fresh = requireNotNull(model.confirmation)
        close(); assertSame(fresh, model.confirmation)
        var sends = 0
        gateway.onDockerResourceChange = { sends++; ApiResult.Transport(null) }
        model.change(fresh); model.change(fresh); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `old login withdrawal leaves current resource confirmation intact`() = runTest {
        val old = review()
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val owner = requireNotNull(model.state.owner)
        model.requestChange(owner, facts, null, change)
        val fresh = requireNotNull(model.confirmation)
        model.withdrawReview(old.owner); assertSame(fresh, model.confirmation)
        model.withdrawReview(owner); assertNull(model.confirmation)
    }
    @Test fun `refresh and dismissal reject old callback even when identical review reopens`() = runTest {
        val old = review(); var sends = 0
        gateway.onDockerResourceChange = { sends++; ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle(); assertNull(model.confirmation)
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        val fresh = requireNotNull(model.confirmation)
        model.change(old); advanceUntilIdle(); assertEquals(0, sends)
        model.dismissConfirmation(fresh); model.change(fresh); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `stopped review cannot execute and fresh confirmation is consumed once`() = runTest {
        val old = review(); model.stop(); model.change(old); advanceUntilIdle()
        model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        val fresh = requireNotNull(model.confirmation); var sends = 0
        gateway.onDockerResourceChange = { sends++; ApiResult.Transport(null) }
        model.change(fresh); model.change(fresh); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(1, model.state.pending.size)
    }
    @Test fun `old stats timer cannot query a newly selected container`() = runTest {
        login(); runCurrent()
        val first = "a".repeat(64); val second = "b".repeat(64)
        val reads = mutableListOf<String>()
        gateway.onDockerContainerDetails = { id -> ApiResult.Success(DockerContainerDetails(id, "review", "image", "", "running", "running", "", "", "",
            emptyList(), emptyList(), emptyList(), emptyList(), emptyMap())) }
        gateway.onDockerContainerStats = { id -> reads += id; ApiResult.Success(DockerContainerStats(id, "0", "0", "0", "0")) }
        model.select(DockerResourceKind.Containers, first); runCurrent()
        val timer = launch { model.observeStats { true } }
        runCurrent()
        model.select(DockerResourceKind.Containers, second); runCurrent()
        advanceTimeBy(3_000); runCurrent(); timer.join()
        assertEquals(listOf(first, second), reads)
    }
    @Test fun `force edits replace approval and switching target revokes it`() = runTest {
        model.dismissConfirmation(review())
        val id = "a".repeat(64)
        gateway.onDockerContainerDetails = { target -> ApiResult.Success(DockerContainerDetails(target, "review", "image", "", "running", "running", "", "", "",
            emptyList(), emptyList(), emptyList(), emptyList(), emptyMap())) }
        gateway.onDockerContainerStats = { target -> ApiResult.Success(DockerContainerStats(target, "0", "0", "0", "0")) }
        model.select(DockerResourceKind.Containers, id); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, model.state.target,
            DockerResourceChange(DockerResourceAction.Stop, target = id))
        val old = requireNotNull(model.confirmation)
        model.updateForce(old, true)
        val forced = requireNotNull(model.confirmation)
        assertNotSame(old, forced); assertTrue(forced.change.force)
        model.updateForce(old, false); assertSame(forced, model.confirmation)
        model.select(DockerResourceKind.Containers, "b".repeat(64)); advanceUntilIdle()
        assertNull(model.confirmation)
        model.updateForce(forced, false); assertNull(model.confirmation)
        model.change(forced); advanceUntilIdle(); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `stats timer rechecks foreground after delay`() = runTest {
        login(); runCurrent()
        val id = "a".repeat(64)
        var foreground = true; var reads = 0
        gateway.onDockerContainerDetails = { ApiResult.Success(DockerContainerDetails(id, "review", "image", "", "running", "running", "", "", "",
            emptyList(), emptyList(), emptyList(), emptyList(), emptyMap())) }
        gateway.onDockerContainerStats = { reads++; ApiResult.Success(DockerContainerStats(id, "0", "0", "0", "0")) }
        model.select(DockerResourceKind.Containers, id); runCurrent()
        val timer = launch { model.observeStats { foreground } }; runCurrent()
        foreground = false
        advanceTimeBy(3_000); runCurrent(); timer.join()
        assertEquals(1, reads); assertFalse(model.state.busy)
    }
    @Test fun `identical stats replies continue until foreground is withdrawn`() = runTest {
        login(); runCurrent()
        val id = "a".repeat(64)
        var foreground = true; var reads = 0
        gateway.onDockerContainerDetails = { ApiResult.Success(DockerContainerDetails(id, "review", "image", "", "running", "running", "", "", "",
            emptyList(), emptyList(), emptyList(), emptyList(), emptyMap())) }
        gateway.onDockerContainerStats = { reads++; ApiResult.Success(DockerContainerStats(id, "0", "0", "0", "0")) }
        model.select(DockerResourceKind.Containers, id); runCurrent()
        val timer = launch { model.observeStats { foreground } }; runCurrent()
        advanceTimeBy(6_000); runCurrent()
        assertEquals(3, reads)
        foreground = false
        advanceTimeBy(3_000); runCurrent(); timer.join()
        assertEquals(3, reads)
    }
    @Test fun `reselecting same container invalidates old stats deadline`() = runTest {
        login(); runCurrent()
        val id = "a".repeat(64)
        var reads = 0
        gateway.onDockerContainerDetails = { ApiResult.Success(DockerContainerDetails(id, "review", "image", "", "running", "running", "", "", "",
            emptyList(), emptyList(), emptyList(), emptyList(), emptyMap())) }
        gateway.onDockerContainerStats = { reads++; ApiResult.Success(DockerContainerStats(id, "0", "0", "0", "0")) }
        model.select(DockerResourceKind.Containers, id); runCurrent()
        val timer = launch { model.observeStats { true } }; runCurrent()
        advanceTimeBy(2_000)
        model.select(DockerResourceKind.Containers, id); runCurrent()
        advanceTimeBy(1_000); runCurrent()
        assertEquals(2, reads)
        timer.cancelAndJoin()
    }
    private val successfulReceipt = DockerOperation(true, "", listOf("created"), false)
    private fun submitVolume() {
        model.requestChange(requireNotNull(model.state.owner), facts, null, change)
        model.change(requireNotNull(model.confirmation))
    }
    @Test fun `resource success uses one verification and keeps receipt`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var reads = 0
        gateway.onDockerStatus = { reads++; check(reads <= 3) { "redundant read" }; ApiResult.Success(facts.status) }
        gateway.onDockerResourceChange = { ApiResult.Success(successfulReceipt) }
        submitVolume(); advanceUntilIdle()
        assertEquals(3, reads); assertEquals(successfulReceipt, model.state.result)
        assertEquals(1, model.state.saved); assertEquals(facts, model.state.facts); assertNull(model.state.problem)
    }
    @Test fun `failed resource verification retains successful receipt and closes submitted editor`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        gateway.onDockerResourceChange = { gateway.onDockerStatus = { error("private verification error") }; ApiResult.Success(successfulReceipt) }
        submitVolume(); advanceUntilIdle()
        assertEquals(successfulReceipt, model.state.result); assertEquals(1, model.state.saved)
        assertNull(model.state.facts); assertEquals(1, model.state.pending.size)
        assertEquals("docker.control.refresh_failed", model.state.problem)
    }

    @Test fun `read refresh and explicit adoption keep known receipt without replaying write`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var sends = 0
        gateway.onDockerResourceChange = { sends++; gateway.onDockerStatus = { ApiResult.Transport(null) }; ApiResult.Success(successfulReceipt) }
        submitVolume(); advanceUntilIdle()
        assertEquals(successfulReceipt, model.state.result)
        gateway.onDockerStatus = { ApiResult.Success(facts.status) }
        model.refresh(); advanceUntilIdle()
        assertEquals(successfulReceipt, model.state.result); assertEquals(1, model.state.pending.size)
        model.accept(model.state.pending.single()); advanceUntilIdle()
        assertEquals(successfulReceipt, model.state.result); assertTrue(model.state.pending.isEmpty()); assertEquals(1, sends)
    }

}
