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
        model.requestChange(requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Stop))
        model.change(requireNotNull(model.changeReview)); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertEquals("file-services.smb.unverified", model.state.problem)
        assertEquals(1, sends); assertEquals(1, model.state.pending.size)
    }

    @Test fun `failed adoption cannot be hidden by a second successful read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts)
        val marker = journal.begin(requireNotNull(model.state.owner), SmbChange(SmbChangeKind.Stop))
        var reads = 0
        gateway.onSmbCapabilities = {
            reads++
            if (reads == 1) ApiResult.Transport(null) else ApiResult.Success(facts.capabilities)
        }
        model.accept(marker); advanceUntilIdle()
        assertEquals("file-services.smb.unverified", model.state.problem)
        assertNull(model.state.facts); assertEquals(listOf(marker), model.state.pending)
        assertEquals(1, reads); assertEquals(0, sends); assertFalse(model.state.busy)
    }
    private suspend fun TestScope.review(): SmbChangeReview {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, SmbChange(SmbChangeKind.Password, "review-user"))
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
        model.requestChange(requireNotNull(model.state.owner), facts, old.confirmation.change)
        val fresh = requireNotNull(model.changeReview)
        val password = "review-password".toCharArray()
        model.change(old, password); advanceUntilIdle()
        assertEquals(0, sends); assertTrue(password.all { it == '\u0000' }); assertSame(fresh, model.changeReview)
        model.dismissConfirmation(); model.change(fresh, password); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `stop revokes review and fresh confirmation sends once then clears password`() = runTest {
        val old = review(); model.stop()
        val rejected = "review-password".toCharArray()
        model.change(old, rejected); advanceUntilIdle(); assertEquals(0, sends); assertTrue(rejected.all { it == '\u0000' })
        model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, old.confirmation.change)
        val fresh = requireNotNull(model.changeReview); val password = "review-password".toCharArray()
        model.change(fresh, password); model.change(fresh, "duplicate-password".toCharArray()); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(1, model.state.pending.size); assertTrue(password.all { it == '\u0000' })
    }

    @Test fun `successful adoption uses the exact verified facts and does not read twice or replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val marker = journal.begin(requireNotNull(model.state.owner), SmbChange(SmbChangeKind.Stop))
        var reads = 0
        gateway.onSmbCapabilities = {
            reads++
            if (reads == 1) ApiResult.Success(facts.capabilities) else ApiResult.Transport(null)
        }
        model.accept(marker); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
        assertTrue(model.state.pending.isEmpty()); assertEquals(1, reads)
        assertEquals(0, sends); assertFalse(model.state.busy)
    }
}
