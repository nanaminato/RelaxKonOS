package app.relaxkonos.mobile.ui.manage.certificates

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CertificatesViewModelTest {
    private class JournalStorage : InstallationRequestStorage {
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
    private val index = OperationIndex(IndexStorage())
    private val journal = CertificateRequestJournal(JournalStorage())
    private val repository = CertificateRepository(gateway, session, index, journal,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()),
            UsageMemoryStore(InMemoryUsageMemoryStorage())))
    private val store = ViewModelStore()
    private lateinit var model: CertificatesViewModel
    private val draft = CertificateDraft(domainsText = "host.example.test", email = "alice@example.test",
        acceptedTerms = true, reachable = true)
    private val ready = CertificatePreflight(true, true, false, emptyList(), "", false)
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = CertificatesViewModel(session, repository, index, ElevationAnswerProvider.Declines)
        store.put("certificates", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.CERTIFICATES), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "pw".toCharArray()) {}
    }
    @Test fun `repeated preflight exception invalidates the old permission and cannot submit`() = runTest {
        login(); runCurrent(); model.create(false); model.update(draft)
        gateway.onCertificatePreflight = { _, _ -> ApiResult.Success(ready) }
        model.preflight(); advanceUntilIdle()
        assertNotNull(model.state.preflight); assertNotNull(model.state.preflightAtMillis)
        gateway.onCertificatePreflight = { _, _ -> throw IllegalStateException("private detail") }
        model.preflight(); advanceUntilIdle()
        assertNull(model.state.preflight); assertNull(model.state.preflightAtMillis)
        assertEquals(draft, model.state.draft); assertFalse(model.state.busy); assertFalse(model.state.uncertain)
        assertEquals("certificate.read_failed", model.state.problemCode)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        model.submitDraft(); advanceUntilIdle()
        assertEquals(0, sends)
    }
    @Test fun `unexpected submission retains a locked draft and the original recovery intent`() = runTest {
        login(); runCurrent(); model.create(true)
        val requested = draft.copy(selfSigned = true)
        model.update(requested)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; throw IllegalStateException("private detail") }
        model.submitDraft(); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(requested, model.state.draft)
        assertTrue(model.state.uncertain); assertTrue(model.state.draftLocked); assertFalse(model.state.busy)
        assertEquals(1, model.state.pending.size); assertTrue(model.state.pending.single().attempted)
        model.update(requested.copy(domainsText = "different.example.test"))
        assertEquals(requested, model.state.draft); assertEquals(1, sends)
    }
    @Test fun `authoritative submission refusal unlocks the preserved draft for correction`() = runTest {
        login(); runCurrent(); model.create(true)
        val requested = draft.copy(selfSigned = true)
        model.update(requested)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Problem(403, "certificate.admin_required", null) }
        model.submitDraft(); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(requested, model.state.draft)
        assertFalse(model.state.uncertain); assertFalse(model.state.draftLocked); assertFalse(model.state.busy)
        assertTrue(model.state.pending.isEmpty()); assertEquals("certificate.admin_required", model.state.problemCode)
        val corrected = requested.copy(domainsText = "different.example.test")
        model.update(corrected)
        assertEquals(corrected, model.state.draft); assertEquals(1, sends)
    }
    @Test fun `old preflight response cannot grant permission to a new identity draft`() = runTest {
        login(); runCurrent(); model.create(false); model.update(draft)
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        gateway.onCertificatePreflight = { _, _ -> entered.complete(Unit); release.await(); ApiResult.Success(ready) }
        model.preflight(); runCurrent(); entered.await()
        assertTrue(model.state.busy)
        session.clearSession(); runCurrent()
        assertNull(model.state.draft); assertFalse(model.state.busy)
        login(); runCurrent(); model.create(false)
        val next = draft.copy(domainsText = "new-owner.example.test")
        model.update(next)
        release.complete(Unit); advanceUntilIdle()
        assertEquals(next, model.state.draft); assertNull(model.state.preflight)
        assertNull(model.state.preflightAtMillis); assertFalse(model.state.busy)
        assertNull(model.state.problemCode); assertFalse(model.state.uncertain)
    }
    @Test fun `changed form invalidates preflight and failed read retains the corrected draft`() = runTest {
        login(); runCurrent(); model.create(false); model.update(draft)
        gateway.onCertificatePreflight = { _, _ -> ApiResult.Success(ready) }
        model.preflight(); advanceUntilIdle()
        val changed = draft.copy(domainsText = "other.example.test")
        model.update(changed)
        assertNull(model.state.preflight); assertNull(model.state.preflightAtMillis)
        gateway.onCertificatePreflight = { _, _ -> ApiResult.Transport(null) }
        model.preflight(); advanceUntilIdle()
        assertEquals(changed, model.state.draft); assertFalse(model.state.draftLocked)
        assertFalse(model.state.uncertain); assertFalse(model.state.busy)
        assertEquals("certificate.read_failed", model.state.problemCode)
    }
}
