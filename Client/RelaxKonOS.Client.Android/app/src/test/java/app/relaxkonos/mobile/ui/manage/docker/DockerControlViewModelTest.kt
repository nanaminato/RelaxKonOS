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
    private val installationJournal = InstallationRequestJournal(Storage())
    private val installations = InstallationRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage())),
        index, installationJournal)
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
    private fun restoreInstallation(id: String, identified: Boolean) {
        val form = requireNotNull(model.requestRecovery(requireNotNull(model.state.owner)))
        model.changeRecoveryId(form, id)
        if (identified) model.identifyRecovery(form, form.draft, true)
        model.submitRecovery(form, form.draft)
    }
    @Test fun `recovery is bound to form and exact input and current submission reads once`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val owner = requireNotNull(model.state.owner)
        val old = requireNotNull(model.requestRecovery(owner))
        model.changeRecoveryId(old, task.operationId)
        val oldInput = old.draft
        val nextId = "22222222-2222-2222-2222-222222222222"
        model.changeRecoveryId(old, nextId)
        var reads = 0
        gateway.onInstallation = { id -> reads++; ApiResult.Success(task.copy(operationId = id)) }
        model.submitRecovery(old, oldInput); advanceUntilIdle(); assertEquals(0, reads)
        model.dismissRecovery(old)
        val fresh = requireNotNull(model.requestRecovery(owner))
        model.dismissRecovery(old); assertSame(fresh, model.recoveryForm)
        model.submitRecovery(old, old.draft); advanceUntilIdle(); assertEquals(0, reads)
        val input = fresh.draft
        model.submitRecovery(fresh, input); model.submitRecovery(fresh, input); advanceUntilIdle()
        assertEquals(1, reads); assertEquals(nextId, model.state.installation?.operationId)
    }
    @Test fun `identification resets for changed id and old checkbox cannot identify new input`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        val pending = installationJournal.begin(owner, InstallationService.Docker, InstallationKind.Install, "original")
        installationJournal.update(pending.copy(attempted = true))
        model.refresh(); advanceUntilIdle()
        val form = requireNotNull(model.requestRecovery(owner))
        model.changeRecoveryId(form, task.operationId)
        val old = form.draft
        model.identifyRecovery(form, old, true); assertTrue(form.draft.identified)
        model.changeRecoveryId(form, "22222222-2222-2222-2222-222222222222")
        assertFalse(form.draft.identified)
        model.identifyRecovery(form, old, true); assertFalse(form.draft.identified)
        var reads = 0
        gateway.onInstallation = { id -> reads++; ApiResult.Success(task.copy(operationId = id)) }
        model.submitRecovery(form, form.draft); advanceUntilIdle(); assertEquals(0, reads)
        model.identifyRecovery(form, form.draft, true)
        model.submitRecovery(form, form.draft); advanceUntilIdle()
        assertEquals(1, reads); assertTrue(installationJournal.pending(owner).isEmpty())
    }
    @Test fun `replaced original pending request is neither queried nor cleared`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        val original = installationJournal.begin(owner, InstallationService.Docker, InstallationKind.Install, "original")
        installationJournal.update(original.copy(attempted = true))
        model.refresh(); advanceUntilIdle()
        val form = requireNotNull(model.requestRecovery(owner))
        model.changeRecoveryId(form, task.operationId); model.identifyRecovery(form, form.draft, true)
        installationJournal.complete(original)
        val replacement = installationJournal.begin(owner, InstallationService.Docker, InstallationKind.Repair, "replacement")
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        model.submitRecovery(form, form.draft); advanceUntilIdle()
        assertEquals(0, reads); assertEquals(listOf(replacement), installationJournal.pending(owner))
        assertTrue(model.state.pendingInstallation); assertFalse(model.state.busy)
    }
    @Test fun `withdrawal and explicit refresh revoke recovery forms`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val owner = requireNotNull(model.state.owner)
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        val old = requireNotNull(model.requestRecovery(owner)); model.changeRecoveryId(old, task.operationId)
        model.withdrawReviews(owner); model.submitRecovery(old, old.draft); advanceUntilIdle(); assertEquals(0, reads)
        val fresh = requireNotNull(model.requestRecovery(owner)); model.changeRecoveryId(fresh, task.operationId)
        model.refresh(); advanceUntilIdle()
        model.submitRecovery(fresh, fresh.draft); advanceUntilIdle(); assertNull(model.recoveryForm); assertEquals(0, reads)
    }
    @Test fun `same account new login rejects old recovery before collection`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val old = requireNotNull(model.requestRecovery(requireNotNull(model.state.owner)))
        model.changeRecoveryId(old, task.operationId)
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        login(); model.submitRecovery(old, old.draft); runCurrent()
        assertEquals(0, reads); assertNull(model.recoveryForm)
    }
    @Test fun `installation observation pauses for recovery and resumes on close`() = runTest {
        login(); runCurrent(); gateway.onInstallation = { ApiResult.Success(task) }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        val form = requireNotNull(model.requestRecovery(requireNotNull(model.state.owner)))
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        assertNull(model.installationCancellation)
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task.copy(state = InstallationState.Succeeded)) }
        model.observeInstallation { true }; advanceUntilIdle(); assertEquals(0, reads)
        model.dismissRecovery(form)
        model.observeInstallation { true }; advanceUntilIdle(); assertEquals(1, reads)
    }
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
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
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
    private suspend fun TestScope.review(): ControlConfirmation {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        return requireNotNull(model.confirmation)
    }
    @Test fun `new login with identical engine facts rejects old confirmation`() = runTest {
        val old = review(); var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; ApiResult.Transport(null) }
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.change(old); advanceUntilIdle(); assertEquals(0, sends); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `old close cannot dismiss reopened engine review`() = runTest {
        val old = review()
        val close = { model.dismissConfirmation(old) }
        close()
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        val fresh = requireNotNull(model.confirmation)
        close(); assertSame(fresh, model.confirmation)
        var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; ApiResult.Transport(null) }
        model.change(fresh); model.change(fresh); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `old close cannot dismiss reopened installation cancellation`() = runTest {
        val old = cancellationReview()
        val close = { model.dismissInstallationCancellation(old) }
        close()
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        val fresh = requireNotNull(model.installationCancellation)
        close(); assertSame(fresh, model.installationCancellation)
        var sends = 0
        gateway.onCancelInstallation = { _, _ -> sends++; ApiResult.Success(task.copy(state = InstallationState.Cancelled)) }
        model.cancelInstall(fresh); model.cancelInstall(fresh); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `old login withdrawal leaves current control and cancellation reviews intact`() = runTest {
        val old = review()
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val owner = requireNotNull(model.state.owner)
        model.requestChange(owner, facts, change)
        val fresh = requireNotNull(model.confirmation)
        model.withdrawReviews(old.owner); assertSame(fresh, model.confirmation)
        model.withdrawReviews(owner); assertNull(model.confirmation)
        gateway.onInstallation = { ApiResult.Success(task) }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        model.requestInstallationCancellation(owner, task)
        val cancel = requireNotNull(model.installationCancellation)
        model.withdrawReviews(old.owner); assertSame(cancel, model.installationCancellation)
        model.withdrawReviews(owner); assertNull(model.installationCancellation)
    }
    @Test fun `refresh and dismissal invalidate old confirmation even after reopening`() = runTest {
        val old = review(); var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle(); assertNull(model.confirmation)
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        val fresh = requireNotNull(model.confirmation)
        model.change(old); advanceUntilIdle(); assertEquals(0, sends)
        model.dismissConfirmation(fresh); model.change(fresh); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `stop invalidates review and fresh confirmation is consumed once`() = runTest {
        val old = review(); model.stop(); model.change(old); advanceUntilIdle()
        model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        val fresh = requireNotNull(model.confirmation); var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; ApiResult.Transport(null) }
        model.change(fresh); model.change(fresh); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(1, model.state.pending.size)
    }
    private suspend fun TestScope.cancellationReview(): DockerInstallationCancellation {
        login(); runCurrent()
        gateway.onInstallation = { ApiResult.Success(task) }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        return requireNotNull(model.installationCancellation)
    }
    @Test fun `explicit recovery requires closing prior review and old cancel cannot target replacement`() = runTest {
        val old = cancellationReview()
        val next = task.copy(operationId = "22222222-2222-2222-2222-222222222222")
        val cancellations = mutableListOf<String>()
        gateway.onInstallation = { ApiResult.Success(next) }
        gateway.onCancelInstallation = { id, _ -> cancellations += id; ApiResult.Success(next.copy(state = InstallationState.Cancelled)) }
        assertNull(model.requestRecovery(old.owner)); assertSame(old, model.installationCancellation)
        model.dismissInstallationCancellation(old)
        restoreInstallation(next.operationId, false); advanceUntilIdle()
        model.cancelInstall(old); advanceUntilIdle(); assertTrue(cancellations.isEmpty())
        model.requestInstallationCancellation(requireNotNull(model.state.owner), next)
        model.cancelInstall(requireNotNull(model.installationCancellation)); advanceUntilIdle()
        assertEquals(listOf(next.operationId), cancellations)
    }
    @Test fun `terminal unverified and noncancellable tasks cannot reopen cancellation`() = runTest {
        val old = cancellationReview()
        for (snapshot in listOf(task.copy(state = InstallationState.Succeeded), task.copy(cancellable = false))) {
            gateway.onInstallation = { ApiResult.Success(snapshot) }
            model.pollInstall(); advanceUntilIdle()
            model.requestInstallationCancellation(requireNotNull(model.state.owner), snapshot)
            assertNull(model.installationCancellation)
            model.cancelInstall(old); advanceUntilIdle()
        }
        gateway.onInstallation = { ApiResult.Transport(null) }
        model.pollInstall(); advanceUntilIdle()
        val unverified = model.state
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        model.cancelInstall(old); advanceUntilIdle()
        assertNull(model.installationCancellation); assertEquals(unverified, model.state)
    }
    @Test fun `same address new login rejects previous cancellation even for identical task`() = runTest {
        val old = cancellationReview(); var sends = 0
        gateway.onCancelInstallation = { _, _ -> sends++; ApiResult.Success(task.copy(state = InstallationState.Cancelled)) }
        login(); runCurrent(); restoreInstallation(task.operationId, false); advanceUntilIdle()
        model.cancelInstall(old); advanceUntilIdle(); assertEquals(0, sends)
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        model.cancelInstall(requireNotNull(model.installationCancellation)); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `refresh and close revoke same-task reviews and fresh cancellation is consumed once`() = runTest {
        val old = cancellationReview(); var sends = 0
        gateway.onCancelInstallation = { _, _ -> sends++; ApiResult.Success(task.copy(state = InstallationState.Cancelled)) }
        model.pollInstall(); advanceUntilIdle()
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        val reopened = requireNotNull(model.installationCancellation)
        model.cancelInstall(old); assertSame(reopened, model.installationCancellation)
        model.dismissInstallationCancellation(reopened); model.cancelInstall(reopened); advanceUntilIdle(); assertEquals(0, sends)
        model.requestInstallationCancellation(requireNotNull(model.state.owner), task)
        val fresh = requireNotNull(model.installationCancellation)
        model.cancelInstall(fresh); model.cancelInstall(fresh); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `installation observation pauses for cancellation review and resumes after dismissal`() = runTest {
        cancellationReview(); var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task.copy(state = InstallationState.Succeeded)) }
        model.observeInstallation { true }; advanceUntilIdle(); assertEquals(0, reads)
        model.dismissInstallationCancellation(requireNotNull(model.installationCancellation))
        model.observeInstallation { true }; advanceUntilIdle(); assertEquals(1, reads)
    }
    @Test fun `old installation timer cannot query a newly selected task`() = runTest {
        login(); runCurrent()
        val next = task.copy(operationId = "22222222-2222-2222-2222-222222222222")
        val reads = mutableListOf<String>()
        gateway.onInstallation = { id -> reads += id; ApiResult.Success(if (id == task.operationId) task else next) }
        restoreInstallation(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }
        runCurrent()
        restoreInstallation(next.operationId, false); runCurrent()
        advanceTimeBy(1_500); runCurrent(); timer.join()
        assertEquals(listOf(task.operationId, next.operationId), reads)
    }
    @Test fun `installation timer rechecks foreground after delay`() = runTest {
        login(); runCurrent()
        var foreground = true; var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        restoreInstallation(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { foreground } }; runCurrent()
        foreground = false
        advanceTimeBy(1_500); runCurrent(); timer.join()
        assertEquals(1, reads); assertFalse(model.state.busy)
    }
    @Test fun `identical running installation replies keep observing until terminal`() = runTest {
        login(); runCurrent()
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(if (reads < 4) task else task.copy(state = InstallationState.Succeeded)) }
        restoreInstallation(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }; runCurrent()
        advanceTimeBy(4_500); runCurrent(); timer.join()
        assertEquals(4, reads); assertEquals(InstallationState.Succeeded, model.state.installation?.state)
    }
    @Test fun `manual reread of same installation invalidates old observation deadline`() = runTest {
        login(); runCurrent()
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(task) }
        restoreInstallation(task.operationId, false); runCurrent()
        val timer = launch { model.observeInstallation { true } }; runCurrent()
        advanceTimeBy(1_000)
        restoreInstallation(task.operationId, false); runCurrent()
        advanceTimeBy(500); runCurrent()
        assertEquals(2, reads)
        timer.cancelAndJoin()
    }
    @Test fun `successful engine change reuses one verification without a second view model read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var reads = 0; var sends = 0
        gateway.onDockerStatus = { reads++; check(reads <= 3) { "redundant read" }; ApiResult.Success(facts.status) }
        gateway.onDockerEngineAction = { _, _ -> sends++; ApiResult.Success(DockerEngineResult(true, "", facts.status)) }
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
        assertEquals(3, reads); assertEquals(1, sends)
        assertEquals(1, model.state.saved); assertEquals(facts, model.state.facts)
        assertNull(model.state.problem); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `failed post-write verification retains success and blocks replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var sends = 0
        gateway.onDockerEngineAction = { _, _ -> sends++; gateway.onDockerStatus = { error("private post-write failure") }; ApiResult.Success(DockerEngineResult(true, "", facts.status)) }
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
        assertEquals(1, model.state.saved); assertNull(model.state.facts)
        assertEquals("docker.control.refresh_failed", model.state.problem); assertEquals(1, model.state.pending.size)
        assertEquals(1, sends); assertFalse(model.state.busy)
    }

    @Test fun `known installation terminal survives engine facts exception`() = runTest {
        login(); runCurrent()
        val terminal = task.copy(state = InstallationState.Succeeded, stage = InstallationStage.Completed, completedAtMillis = 2, cancellable = false)
        gateway.onInstallation = { ApiResult.Success(terminal) }
        gateway.onDockerStatus = { error("private post-installation read failure") }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        assertEquals(terminal, model.state.installation); assertTrue(model.state.installationVerified)
        assertNull(model.state.facts); assertEquals("docker.control.installation_refresh_failed", model.state.problem)
        assertFalse(model.state.busy); assertFalse(model.state.installationLookupFailed)
    }
    @Test fun `known installation terminal survives cancelled verification in active coroutine`() = runTest {
        login(); runCurrent()
        val terminal = task.copy(state = InstallationState.Succeeded, stage = InstallationStage.Completed, completedAtMillis = 2, cancellable = false)
        gateway.onInstallation = { ApiResult.Success(terminal) }
        gateway.onDockerStatus = { throw CancellationException("private verification cancellation") }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        assertEquals(terminal, model.state.installation); assertTrue(model.state.installationVerified)
        assertNull(model.state.facts); assertEquals("docker.control.installation_refresh_failed", model.state.problem)
    }
    @Test fun `installation failure remains visible after both direct and historical fact refresh`() = runTest {
        login(); runCurrent()
        val failed = task.copy(state = InstallationState.Failed, stage = InstallationStage.Failed,
            problemCode = InstallationProblemCodes.FAILED, completedAtMillis = 2, cancellable = false)
        gateway.onInstallation = { ApiResult.Success(failed) }
        restoreInstallation(task.operationId, false); advanceUntilIdle()
        assertEquals(failed, model.state.installation); assertTrue(model.state.installationVerified)
        assertEquals(facts, model.state.facts); assertEquals(failed.problemCode, model.state.problem)
        model.refresh(); advanceUntilIdle()
        assertEquals(failed, model.state.installation); assertEquals(failed.problemCode, model.state.problem)
        assertEquals(facts, model.state.facts)
    }

}
