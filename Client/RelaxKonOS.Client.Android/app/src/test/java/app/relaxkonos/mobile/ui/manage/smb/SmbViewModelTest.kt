package app.relaxkonos.mobile.ui.manage.smb

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class SmbViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private class IndexStorage : OperationIndexStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val journal = SmbMutationJournal(Storage())
    private val index = OperationIndex(IndexStorage())
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val smb = SmbRepository(gateway, session, elevation, journal)
    private val installations = InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage()))
    private val facts = SmbFacts(SmbCapabilities(true, true, true, true, false, null),
        SmbStatus(SmbRuntimeState.Running, "4", true, true, null), emptyList(), listOf(SmbUser("review-user", true, true)),
        SmbConnection("review-host", 445, "prefix", "smb://review-host"))
    private val store = ViewModelStore()
    private lateinit var model: SmbViewModel
    private var sends = 0
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = SmbViewModel(session, smb, installations, index, ElevationAnswerProvider.Declines)
        model.setInteractionActive(true)
        store.put("smb", model)
        gateway.onSmbCapabilities = { ApiResult.Success(facts.capabilities) }
        gateway.onSmbStatus = { ApiResult.Success(facts.status) }
        gateway.onSmbConnection = { ApiResult.Success(facts.connection) }
        gateway.onSmbShares = { ApiResult.Success(facts.shares!!) }
        gateway.onSmbUsers = { ApiResult.Success(facts.users!!) }
        gateway.onActiveInstallation = { ApiResult.Success(null) }
        gateway.onSmbChange = { _, _ -> sends++; ApiResult.Transport(null) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.FILE_SERVICES), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://review.invalid"), "review-user", "test".toCharArray()) {}
    }

    @Test fun `cancelled refresh clears previously verified facts and permits explicit recovery`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts)
        gateway.onSmbCapabilities = { throw CancellationException("private cancelled read") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertFalse(model.state.installationVerified)
        assertEquals("file-services.smb.unverified", model.state.problem)
        gateway.onSmbCapabilities = { ApiResult.Success(facts.capabilities) }
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
        assertEquals(0, sends)
    }

    @Test fun `cancelled change retains recovery marker and revokes old facts`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        gateway.onSmbChange = { _, _ -> sends++; throw CancellationException("private cancelled write") }
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Stop))
        model.change(requireNotNull(model.changeReview)); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertEquals("file-services.smb.unverified", model.state.problem)
        assertEquals(1, sends); assertEquals(1, model.state.pending.size)
    }

    @Test fun `failed adoption cannot be hidden by a second successful read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts)
        journal.begin(requireNotNull(model.state.owner), SmbChange(SmbChangeKind.Stop))
        model.refresh(); advanceUntilIdle()
        val marker = model.state.pending.single()
        var reads = 0
        gateway.onSmbCapabilities = {
            reads++
            if (reads == 1) ApiResult.Transport(null) else ApiResult.Success(facts.capabilities)
        }
        model.accept(model.interactionVersion, requireNotNull(model.state.owner), marker); advanceUntilIdle()
        assertEquals("file-services.smb.unverified", model.state.problem)
        assertNull(model.state.facts); assertEquals(listOf(marker), model.state.pending)
        assertEquals(1, reads); assertEquals(0, sends); assertFalse(model.state.busy)
    }
    @Test fun `left page cannot adopt a recovery marker`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        journal.begin(owner, SmbChange(SmbChangeKind.Stop))
        model.refresh(); advanceUntilIdle()
        val marker = model.state.pending.single()
        model.stop()
        var reads = 0
        gateway.onSmbCapabilities = { reads++; ApiResult.Success(facts.capabilities) }
        model.accept(model.interactionVersion, requireNotNull(model.state.owner), marker); advanceUntilIdle()
        assertEquals(0, reads); assertEquals(listOf(marker), journal.pending(owner))
    }
    @Test fun `left page cannot open a new mutation review after a read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.stop(); model.refresh(); advanceUntilIdle()
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Stop))
        assertNull(model.changeReview)
        assertEquals(0, sends)
    }
    @Test fun `old interaction cannot open mutation review after resume and rejects passwords safely`() = runTest {
        val old = review(); val version = model.interactionVersion
        model.setInteractionActive(false); model.setInteractionActive(true)
        model.requestChange(version, old.owner, facts, old.confirmation.change)
        assertNull(model.changeReview)
        model.requestChange(model.interactionVersion, old.owner, facts, old.confirmation.change)
        val fresh = requireNotNull(model.changeReview)
        val password = "review-password".toCharArray()
        model.change(old, password); advanceUntilIdle()
        assertTrue(password.all { it == '\u0000' }); assertSame(fresh, model.changeReview); assertEquals(0, sends)
    }
    @Test fun `adoption requires current lifecycle owner and displayed marker`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        journal.begin(owner, SmbChange(SmbChangeKind.Stop))
        model.refresh(); advanceUntilIdle()
        val old = model.state.pending.single(); val version = model.interactionVersion
        model.setInteractionActive(false); model.setInteractionActive(true)
        model.accept(version, owner, old); advanceUntilIdle()
        assertEquals(1, journal.pending(owner).size)
        model.refresh(); advanceUntilIdle()
        val fresh = model.state.pending.single()
        val feedback = model.state.feedbackVersion
        model.accept(model.interactionVersion, owner, old); advanceUntilIdle()
        assertEquals(feedback, model.state.feedbackVersion); assertEquals(facts, model.state.facts); assertNull(model.state.problem)
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.accept(model.interactionVersion, owner, model.state.pending.single()); advanceUntilIdle()
        assertEquals(1, journal.pending(requireNotNull(model.state.owner)).size)
        assertNotSame(old, fresh)
        model.accept(model.interactionVersion, requireNotNull(model.state.owner), model.state.pending.single()); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty()); assertEquals(facts, model.state.facts); assertNull(model.state.problem)
    }
    private suspend fun TestScope.review(): SmbChangeReview {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Password, "review-user"))
        return requireNotNull(model.changeReview)
    }
    @Test fun `replacement task withdraws cancellation and old callback cannot cancel new task`() = runTest {
        login(); runCurrent()
        val owner = requireNotNull(model.state.owner)
        val first = InstallationOperation("11111111-1111-1111-1111-111111111111", InstallationService.Smb, InstallationKind.Install,
            InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
        val second = first.copy(operationId = "22222222-2222-2222-2222-222222222222")
        val tasks = listOf(first, second).associateBy { it.operationId }
        val cancellations = mutableListOf<String>()
        gateway.onInstallation = { ApiResult.Success(requireNotNull(tasks[it])) }
        gateway.onCancelInstallation = { id, _ -> cancellations += id; ApiResult.Success(requireNotNull(tasks[id]).copy(state = InstallationState.Cancelled)) }
        model.recoverInstall(first.operationId, false); advanceUntilIdle()
        model.requestInstallationCancellation(owner, first)
        val old = requireNotNull(model.installationCancellation)
        model.recoverInstall(second.operationId, false); advanceUntilIdle(); assertNull(model.installationCancellation)
        model.cancelInstall(old); advanceUntilIdle(); assertTrue(cancellations.isEmpty())
        model.requestInstallationCancellation(owner, second)
        val fresh = requireNotNull(model.installationCancellation)
        model.cancelInstall(fresh); model.cancelInstall(fresh); advanceUntilIdle()
        assertEquals(listOf(second.operationId), cancellations)
    }
    @Test fun `installation timer rechecks foreground and original task after waiting`() = runTest {
        login(); runCurrent()
        val first = InstallationOperation("11111111-1111-1111-1111-111111111111", InstallationService.Smb, InstallationKind.Install,
            InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
        val second = first.copy(operationId = "22222222-2222-2222-2222-222222222222")
        var reads = 0
        gateway.onInstallation = { id -> reads++; ApiResult.Success(if (id == first.operationId) first else second) }
        model.recoverInstall(first.operationId, false); advanceUntilIdle()
        var foreground = true
        val timer = backgroundScope.launch { model.observeInstallation { foreground } }; runCurrent()
        foreground = false; advanceTimeBy(1500); runCurrent(); assertTrue(timer.isCompleted); assertEquals(1, reads)
        val old = backgroundScope.launch { model.observeInstallation { true } }; runCurrent()
        model.recoverInstall(second.operationId, false); runCurrent()
        model.recoverInstall(first.operationId, false); runCurrent()
        advanceTimeBy(1500); runCurrent(); assertTrue(old.isCompleted); assertEquals(3, reads)
    }
    @Test fun `new login rejects old confirmation and wipes supplied password`() = runTest {
        val old = review(); login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val password = "review-password".toCharArray()
        model.change(old, password); advanceUntilIdle()
        assertEquals(0, sends); assertTrue(password.all { it == '\u0000' }); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `refresh and dismissal cannot reuse old approval for identical change`() = runTest {
        val old = review(); model.refresh(); advanceUntilIdle(); assertNull(model.changeReview)
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, old.confirmation.change)
        val fresh = requireNotNull(model.changeReview)
        val password = "review-password".toCharArray()
        model.change(old, password); advanceUntilIdle()
        assertEquals(0, sends); assertTrue(password.all { it == '\u0000' }); assertSame(fresh, model.changeReview)
        model.dismissConfirmation(fresh); model.change(fresh, password); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `stop revokes review and fresh confirmation sends once then clears password`() = runTest {
        val old = review(); model.stop()
        val rejected = "review-password".toCharArray()
        model.change(old, rejected); advanceUntilIdle(); assertEquals(0, sends); assertTrue(rejected.all { it == '\u0000' })
        model.setInteractionActive(true)
        model.refresh(); advanceUntilIdle()
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, old.confirmation.change)
        val fresh = requireNotNull(model.changeReview); val password = "review-password".toCharArray()
        model.change(fresh, password); model.change(fresh, "duplicate-password".toCharArray()); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(1, model.state.pending.size); assertTrue(password.all { it == '\u0000' })
    }

    @Test fun `successful adoption uses the exact verified facts and does not read twice or replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        journal.begin(requireNotNull(model.state.owner), SmbChange(SmbChangeKind.Stop))
        model.refresh(); advanceUntilIdle()
        val marker = model.state.pending.single()
        var reads = 0
        gateway.onSmbCapabilities = {
            reads++
            if (reads == 1) ApiResult.Success(facts.capabilities) else ApiResult.Transport(null)
        }
        model.accept(model.interactionVersion, requireNotNull(model.state.owner), marker); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
        assertTrue(model.state.pending.isEmpty()); assertEquals(1, reads)
        assertEquals(0, sends); assertFalse(model.state.busy)
    }
    private val receipt = SmbReceipt("11111111-1111-1111-1111-111111111111", true, null)
    private fun submitStop() {
        model.requestChange(model.interactionVersion, requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Stop))
        model.change(requireNotNull(model.changeReview))
    }
    @Test fun `successful change uses one verification and preserves receipt`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var reads = 0
        gateway.onSmbCapabilities = { reads++; check(reads <= 3) { "redundant read" }; ApiResult.Success(facts.capabilities) }
        gateway.onSmbChange = { _, _ -> sends++; ApiResult.Success(receipt) }
        submitStop(); advanceUntilIdle()
        assertEquals(3, reads); assertEquals(1, sends)
        assertEquals(receipt, model.state.receipt); assertEquals(1, model.state.saved)
        assertEquals(facts, model.state.facts); assertNull(model.state.problem); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `failed verification cannot erase known receipt or keep submitted draft open`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        gateway.onSmbChange = { _, _ -> sends++; gateway.onSmbCapabilities = { error("private verification failure") }; ApiResult.Success(receipt) }
        submitStop(); advanceUntilIdle()
        assertEquals(receipt, model.state.receipt); assertEquals(1, model.state.saved)
        assertNull(model.state.facts); assertEquals("file-services.smb.refresh_failed", model.state.problem)
        assertEquals(receipt.operationId, model.state.pending.single().receiptId); assertEquals(1, sends)
    }
    @Test fun `a later unknown change cannot keep the earlier successful receipt`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        gateway.onSmbChange = { _, _ -> sends++; ApiResult.Success(receipt) }
        submitStop(); advanceUntilIdle()
        gateway.onSmbChange = { _, _ -> sends++; throw CancellationException("private lost write") }
        submitStop(); advanceUntilIdle()
        assertNull(model.state.receipt); assertEquals(1, model.state.saved)
        assertEquals(1, model.state.pending.size); assertEquals(2, sends)
    }

    private val terminalInstallation = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Smb,
        InstallationKind.Install, InstallationState.Succeeded, InstallationStage.Completed, null, null, 1, 1, 2, false)
    @Test fun `terminal installation remains verified when sharing facts throw`() = runTest {
        login(); runCurrent()
        gateway.onInstallation = { ApiResult.Success(terminalInstallation) }
        gateway.onSmbCapabilities = { error("private sharing facts failure") }
        model.recoverInstall(terminalInstallation.operationId, false); advanceUntilIdle()
        assertEquals(terminalInstallation, model.state.installation); assertTrue(model.state.installationVerified)
        assertNull(model.state.facts); assertEquals("file-services.smb.installation_refresh_failed", model.state.problem)
        assertFalse(model.state.busy); assertEquals(0, sends)
    }
    @Test fun `failed installation retains its reason after successful sharing refresh`() = runTest {
        login(); runCurrent()
        val failed = terminalInstallation.copy(state = InstallationState.Failed, stage = InstallationStage.Failed, problemCode = InstallationProblemCodes.FAILED)
        gateway.onInstallation = { ApiResult.Success(failed) }
        model.recoverInstall(failed.operationId, false); advanceUntilIdle()
        assertEquals(failed, model.state.installation); assertTrue(model.state.installationVerified)
        assertEquals(facts, model.state.facts); assertEquals(failed.problemCode, model.state.problem)
    }
    @Test fun `cancelled sharing read in active coroutine does not revoke terminal installation receipt`() = runTest {
        login(); runCurrent()
        gateway.onInstallation = { ApiResult.Success(terminalInstallation) }
        gateway.onSmbCapabilities = { throw CancellationException("cancelled post-result sharing read") }
        model.recoverInstall(terminalInstallation.operationId, false); advanceUntilIdle()
        assertEquals(terminalInstallation, model.state.installation); assertTrue(model.state.installationVerified)
        assertEquals("file-services.smb.installation_refresh_failed", model.state.problem); assertFalse(model.state.busy)
    }

    @Test fun `stale review dismissal cannot close identical replacement or reopened review`() = runTest {
        val old = review()
        model.requestChange(model.interactionVersion, old.owner, old.confirmation.expected, old.confirmation.change)
        val replacement = requireNotNull(model.changeReview)
        assertNotSame(old.confirmation, replacement.confirmation)
        model.dismissConfirmation(old); assertSame(replacement, model.changeReview)
        model.dismissConfirmation(replacement); assertNull(model.changeReview)
        model.requestChange(model.interactionVersion, old.owner, old.confirmation.expected, old.confirmation.change)
        val reopened = requireNotNull(model.changeReview)
        model.dismissConfirmation(old); assertSame(reopened, model.changeReview)
        model.dismissConfirmation(reopened); assertNull(model.changeReview)
        assertEquals(0, sends)
    }
    @Test fun `stale installation dismissal cannot close replacement cancellation`() = runTest {
        login(); runCurrent()
        val operation = InstallationOperation("55555555-5555-5555-5555-555555555555", InstallationService.Smb,
            InstallationKind.Install, InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
        gateway.onInstallation = { ApiResult.Success(operation) }
        model.recoverInstall(operation.operationId, false); advanceUntilIdle()
        val owner = requireNotNull(model.state.owner)
        model.requestInstallationCancellation(owner, operation)
        val old = requireNotNull(model.installationCancellation)
        model.requestInstallationCancellation(owner, operation)
        val replacement = requireNotNull(model.installationCancellation)
        model.dismissInstallationCancellation(old); assertSame(replacement, model.installationCancellation)
        model.dismissInstallationCancellation(replacement); assertNull(model.installationCancellation)
        assertEquals(0, sends)
    }

    private suspend fun TestScope.installationReview(): SmbInstallationReview {
        gateway.onSmbStatus = { ApiResult.Success(facts.status.copy(state = SmbRuntimeState.NotInstalled)) }
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestInstallation(requireNotNull(model.state.owner), requireNotNull(model.state.facts))
        return requireNotNull(model.installationReview)
    }
    @Test fun `installation approval is consumed once and explicit retry retains original key`() = runTest {
        val keys = mutableListOf<String>()
        gateway.onStartInstallation = { _, _, key -> keys += key; ApiResult.Transport(null) }
        val old = installationReview()
        assertTrue(installations.pending(old.owner).isEmpty())
        model.dismissInstallationReview(old)
        model.requestInstallation(old.owner, old.expected)
        val fresh = requireNotNull(model.installationReview)
        model.dismissInstallationReview(old); assertSame(fresh, model.installationReview)
        assertFalse(model.install(old)); assertSame(fresh, model.installationReview)
        assertTrue(model.install(fresh)); assertFalse(model.install(fresh)); advanceUntilIdle()
        assertEquals(1, keys.size); assertTrue(model.state.pendingInstallation)
        model.requestInstallation(old.owner, requireNotNull(model.state.facts))
        val retry = requireNotNull(model.installationReview)
        assertTrue(retry.retry); assertFalse(model.install(old)); assertSame(retry, model.installationReview)
        assertTrue(model.install(retry)); advanceUntilIdle()
        assertEquals(2, keys.size); assertEquals(keys.first(), keys.last())
    }
    @Test fun `refresh stop and relogin revoke installation approvals without preparing a request`() = runTest {
        val first = installationReview()
        model.refresh(); advanceUntilIdle(); assertFalse(model.install(first))
        model.requestInstallation(first.owner, requireNotNull(model.state.facts))
        val stopped = requireNotNull(model.installationReview)
        model.stop(); assertFalse(model.install(stopped))
        model.refresh(); advanceUntilIdle()
        model.requestInstallation(first.owner, requireNotNull(model.state.facts))
        val oldLogin = requireNotNull(model.installationReview)
        login(); runCurrent(); model.refresh(); advanceUntilIdle(); assertFalse(model.install(oldLogin))
        assertNull(model.installationReview); assertTrue(installations.pending(requireNotNull(model.state.owner)).isEmpty())
    }

}
