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
import kotlinx.coroutines.ExperimentalCoroutinesApi
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
