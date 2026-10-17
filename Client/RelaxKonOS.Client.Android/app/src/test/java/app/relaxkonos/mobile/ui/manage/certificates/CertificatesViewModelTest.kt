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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
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
    private fun submitCurrentDraft() {
        model.submitDraft(session.state.value as SessionState.Active, model.state.draft ?: draft)
    }
    @Test fun `identical failures and new login have distinct feedback events`() = runTest {
        login(); runCurrent()
        gateway.onCertificates = { ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle()
        val first = model.state.feedbackVersion
        model.refresh(); advanceUntilIdle()
        val second = model.state.feedbackVersion
        assertTrue(second > first); assertEquals("certificate.read_failed", model.state.problemCode)
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertTrue(model.state.feedbackVersion > second)
        assertEquals("certificate.read_failed", model.state.problemCode)
        assertFalse(model.state.busy); assertTrue(model.state.list is ApiResult.Transport)
    }
    @Test fun `failed indexed operation restore reports failure and explicit refresh retries original id`() = runTest {
        login(); runCurrent()
        val id = "11111111-1111-1111-1111-111111111111"
        val certificateId = "22222222-2222-2222-2222-222222222222"
        val owner = session.state.value as SessionState.Active
        index.record(owner, OperationDomain.Certificate, certificateId, id)
        gateway.onCertificates = { ApiResult.Success(emptyList()) }
        var reads = 0
        gateway.onCertificateOperation = { requested ->
            assertEquals(id, requested); reads++; ApiResult.Transport(null)
        }
        model.refresh(); advanceUntilIdle()
        assertEquals("certificate.read_failed", model.state.problemCode)
        assertEquals(id, model.state.recoveryOperationId)
        assertFalse(model.state.busy); assertNull(model.state.operation)
        for (failure in listOf(IllegalStateException("private restore detail"), CancellationException("private cancelled restore"))) {
            gateway.onCertificateOperation = { requested -> assertEquals(id, requested); reads++; throw failure }
            model.refresh(); advanceUntilIdle()
            assertEquals(id, model.state.recoveryOperationId)
            assertEquals("certificate.read_failed", model.state.problemCode)
            assertFalse(model.state.busy); assertFalse(model.state.operationVerified)
        }
        gateway.onCertificateOperation = { requested ->
            assertEquals(id, requested); reads++
            ApiResult.Success(CertificateOperation(id, certificateId, CertificateAction.SelfSigned,
                CertificateOperationState.Succeeded, "completed", "", 1, 2))
        }
        model.refresh(); advanceUntilIdle()
        assertEquals(4, reads); assertEquals(id, model.state.operation?.operationId)
        assertTrue(model.state.operationVerified); assertNull(model.state.problemCode)
        assertNull(model.state.recoveryOperationId)
    }
    @Test fun `submission confirmation cannot replace confirmed draft with edited draft`() = runTest {
        login(); runCurrent(); model.create(true)
        val original = draft.copy(selfSigned = true); model.update(original)
        val owner = session.state.value as SessionState.Active
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        val edited = original.copy(domainsText = "edited.example.test")
        model.update(edited)
        model.submitDraft(owner, original); advanceUntilIdle()
        assertEquals(0, sends); assertEquals(edited, model.state.draft)
        model.submitDraft(owner, edited); advanceUntilIdle()
        assertEquals(1, sends)
    }
    @Test fun `submission confirmation cannot survive new login with identical draft`() = runTest {
        login(); runCurrent(); model.create(true)
        val original = draft.copy(selfSigned = true); model.update(original)
        val owner = session.state.value as SessionState.Active
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        login()
        model.submitDraft(owner, original); advanceUntilIdle()
        assertEquals(0, sends)
        model.create(true); model.update(original)
        model.submitDraft(owner, original); advanceUntilIdle()
        assertEquals(0, sends)
        submitCurrentDraft(); advanceUntilIdle(); assertEquals(1, sends)
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
        submitCurrentDraft(); advanceUntilIdle()
        assertEquals(0, sends)
    }
    @Test fun `unexpected submission retains a locked draft and the original recovery intent`() = runTest {
        login(); runCurrent(); model.create(true)
        val requested = draft.copy(selfSigned = true)
        model.update(requested)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; throw IllegalStateException("private detail") }
        submitCurrentDraft(); advanceUntilIdle()
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
        submitCurrentDraft(); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(requested, model.state.draft)
        assertFalse(model.state.uncertain); assertFalse(model.state.draftLocked); assertFalse(model.state.busy)
        assertTrue(model.state.pending.isEmpty()); assertEquals("certificate.admin_required", model.state.problemCode)
        val corrected = requested.copy(domainsText = "different.example.test")
        model.update(corrected)
        assertEquals(corrected, model.state.draft); assertEquals(1, sends)
    }
    @Test fun `cancelled submission exposes the original recovery intent and keeps draft locked`() = runTest {
        login(); runCurrent(); model.create(true)
        val requested = draft.copy(selfSigned = true)
        model.update(requested)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; throw CancellationException("cancelled") }
        submitCurrentDraft(); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(requested, model.state.draft)
        assertFalse(model.state.busy); assertTrue(model.state.uncertain)
        assertTrue(model.state.draftLocked); assertFalse(model.state.operationVerified)
        assertEquals(1, model.state.pending.size); assertTrue(model.state.pending.single().attempted)
        assertNull(model.state.problemCode)
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
    private suspend fun TestScope.acceptedSubmissionWithFailedRefresh(read: suspend () -> ApiResult<List<ManagedCertificate>>) {
        login(); runCurrent(); model.create(true); model.update(draft.copy(selfSigned = true))
        val receipt = CertificateOperation("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
            CertificateAction.SelfSigned, CertificateOperationState.Succeeded, "completed", "", 1, 2)
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Success(receipt) }
        gateway.onCertificates = { read() }
        submitCurrentDraft(); advanceUntilIdle()
        assertEquals(1, sends); assertNull(model.state.draft); assertFalse(model.state.draftLocked)
        assertEquals(receipt, model.state.operation); assertTrue(model.state.operationVerified)
        assertTrue(model.state.pending.isEmpty()); assertFalse(model.state.uncertain); assertFalse(model.state.busy)
        assertEquals("certificate.read_failed", model.state.problemCode)
        submitCurrentDraft(); advanceUntilIdle(); assertEquals(1, sends)
    }
    @Test fun `accepted submission closes draft even when subsequent list refresh throws`() = runTest {
        acceptedSubmissionWithFailedRefresh { throw IllegalStateException("private list detail") }
    }
    @Test fun `accepted submission closes draft even when subsequent list refresh is cancelled`() = runTest {
        acceptedSubmissionWithFailedRefresh { throw CancellationException("private list detail") }
    }
    @Test fun `accepted submission reports subsequent list transport failure without losing receipt`() = runTest {
        acceptedSubmissionWithFailedRefresh { ApiResult.Transport(null) }
    }
    private suspend fun TestScope.loadCertificate() {
        login(); runCurrent()
        val certificate = CertificateWire.certificate(CERTIFICATE_JSON)
        gateway.onCertificates = { ApiResult.Success(listOf(certificate)) }
        gateway.onCertificate = { ApiResult.Success(certificate) }
        gateway.onKestrelDeployment = { ApiResult.Success(CertificateWire.kestrel(KESTREL_JSON)) }
        model.refresh(); advanceUntilIdle(); model.select(certificate.id); advanceUntilIdle()
        assertTrue(model.state.detail is ApiResult.Success)
    }
    @Test fun `refresh exception withdraws previous list detail and deployment facts`() = runTest {
        loadCertificate()
        val captured = (model.state.detail as ApiResult.Success).value
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        gateway.onCertificates = { throw IllegalStateException("private list detail") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.list); assertNull(model.state.detail); assertTrue(model.state.deployments.isEmpty())
        assertEquals("certificate.read_failed", model.state.problemCode); assertFalse(model.state.busy)
        model.action(session.state.value as SessionState.Active, captured, CertificateAction.Delete); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `detail refresh cancellation withdraws previous detail and deployment facts`() = runTest {
        loadCertificate()
        gateway.onCertificate = { throw CancellationException("private detail") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.detail); assertTrue(model.state.deployments.isEmpty())
        assertEquals("certificate.read_failed", model.state.problemCode); assertFalse(model.state.busy)
    }
    @Test fun `deployment refresh exception withdraws previous deployment fact`() = runTest {
        loadCertificate()
        val id = requireNotNull(model.state.selectedId)
        gateway.onKestrelDeployment = { throw IllegalStateException("private deployment") }
        model.inspectDeployment(id); advanceUntilIdle()
        assertNull(model.state.deployments[id]); assertTrue(model.state.detail is ApiResult.Success)
        assertEquals("certificate.read_failed", model.state.problemCode); assertFalse(model.state.busy)
    }
    @Test fun `manual recovery of another operation preserves unsubmitted draft`() = runTest {
        login(); runCurrent(); model.create(true)
        val requested = draft.copy(selfSigned = true); model.update(requested)
        val receipt = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
        gateway.onCertificateOperation = { ApiResult.Success(receipt) }
        model.recover(receipt.operationId); advanceUntilIdle()
        assertEquals(requested, model.state.draft); assertFalse(model.state.draftLocked)
        assertEquals(receipt, model.state.operation); assertTrue(model.state.operationVerified)
    }
    @Test fun `recovering original creation closes locked draft before failed followup refresh`() = runTest {
        login(); runCurrent(); model.create(true); model.update(draft.copy(selfSigned = true))
        gateway.onCertificateMutation = { _, _, _, _ -> throw CancellationException("private submit") }
        submitCurrentDraft(); advanceUntilIdle()
        val pending = model.state.pending.single(); assertTrue(model.state.draftLocked)
        val receipt = CertificateWire.operation(CERTIFICATE_OPERATION_JSON).copy(kind = CertificateAction.SelfSigned, state = CertificateOperationState.Succeeded)
        gateway.onCertificateOperation = { ApiResult.Success(receipt) }
        gateway.onCertificates = { throw IllegalStateException("private list") }
        model.recover(receipt.operationId, pending); advanceUntilIdle()
        assertNull(model.state.draft); assertFalse(model.state.draftLocked); assertTrue(model.state.pending.isEmpty())
        assertEquals(receipt, model.state.operation); assertTrue(model.state.operationVerified)
        assertFalse(model.state.uncertain); assertEquals("certificate.read_failed", model.state.problemCode)
    }
    @Test fun `old confirmation cannot submit while new login awaits collection`() = runTest {
        loadCertificate()
        val originalOwner = session.state.value as SessionState.Active
        val certificate = (model.state.detail as ApiResult.Success).value
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        login()
        model.action(originalOwner, certificate, CertificateAction.Delete); advanceUntilIdle()
        assertEquals(0, sends); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `old confirmation cannot submit after same certificate is read by new login`() = runTest {
        loadCertificate()
        val originalOwner = session.state.value as SessionState.Active
        val certificate = (model.state.detail as ApiResult.Success).value
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        login(); runCurrent(); model.refresh(); advanceUntilIdle(); model.select(certificate.id); advanceUntilIdle()
        model.action(originalOwner, certificate, CertificateAction.Delete); advanceUntilIdle()
        assertEquals(0, sends); assertTrue(model.state.pending.isEmpty())
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Problem(403, "certificate.admin_required", null) }
        model.action(session.state.value as SessionState.Active, certificate, CertificateAction.Delete); advanceUntilIdle()
        assertEquals(1, sends); assertEquals("certificate.admin_required", model.state.problemCode)
    }
    @Test fun `cancel confirmation cannot switch to a different current operation`() = runTest {
        login(); runCurrent()
        val original = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
        val next = original.copy(operationId = "11111111-1111-1111-1111-111111111111")
        gateway.onCertificateOperation = { id -> ApiResult.Success(if (id == original.operationId) original else next) }
        val cancelled = mutableListOf<String>()
        gateway.onCertificateCancel = { id -> cancelled += id; ApiResult.Success(next.copy(state = CertificateOperationState.Cancelled)) }
        model.recover(original.operationId); advanceUntilIdle()
        model.recover(next.operationId); advanceUntilIdle()
        val owner = session.state.value as SessionState.Active
        model.requestCancellation(owner, original); assertNull(model.cancellation)
        assertTrue(cancelled.isEmpty()); assertEquals(next, model.state.operation)
        model.requestCancellation(owner, next); model.cancel(requireNotNull(model.cancellation)); advanceUntilIdle()
        assertEquals(listOf(next.operationId), cancelled)
    }
    @Test fun `failed operation lookup pauses automatic polling and cancel until explicit retry`() = runTest {
        login(); runCurrent()
        val receipt = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
        gateway.onCertificateOperation = { ApiResult.Success(receipt) }
        model.recover(receipt.operationId); advanceUntilIdle()
        var reads = 0; var cancellations = 0
        gateway.onCertificateOperation = { reads++; ApiResult.Transport(null) }
        gateway.onCertificateCancel = { cancellations++; ApiResult.Transport(null) }
        model.poll(); advanceUntilIdle(); assertFalse(model.state.operationVerified)
        model.observeOperation { true }; advanceUntilIdle()
        assertEquals(1, reads); assertEquals("certificate.read_failed", model.state.problemCode)
        model.requestCancellation(session.state.value as SessionState.Active, receipt); assertNull(model.cancellation); assertEquals(0, cancellations)
        gateway.onCertificateOperation = { reads++; ApiResult.Success(receipt) }
        model.poll(); advanceUntilIdle(); assertTrue(model.state.operationVerified); assertEquals(2, reads)
    }
    @Test fun `delayed observation cannot query replacement operation`() = runTest {
        login(); runCurrent()
        val original = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
        val next = original.copy(operationId = "11111111-1111-1111-1111-111111111111")
        var reads = 0
        gateway.onCertificateOperation = { id -> reads++; ApiResult.Success(if (id == original.operationId) original else next) }
        model.recover(original.operationId); advanceUntilIdle()
        val observer = launch { model.observeOperation { true } }; runCurrent()
        model.recover(next.operationId); runCurrent()
        advanceUntilIdle(); observer.join()
        assertEquals(2, reads); assertEquals(next, model.state.operation)
    }
    @Test fun `retry confirmation requires original login even when pending request survives relogin`() = runTest {
        loadCertificate()
        val originalOwner = session.state.value as SessionState.Active
        val certificate = (model.state.detail as ApiResult.Success).value
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        model.action(originalOwner, certificate, CertificateAction.Delete); advanceUntilIdle()
        val pending = model.state.pending.single()
        login()
        model.retry(originalOwner, pending); advanceUntilIdle()
        assertEquals(1, sends)
        model.refresh(); advanceUntilIdle()
        assertEquals(listOf(pending), model.state.pending)
        model.retry(originalOwner, pending); advanceUntilIdle()
        assertEquals(1, sends)
        model.retry(session.state.value as SessionState.Active, pending); advanceUntilIdle()
        assertEquals(2, sends)
        assertEquals(pending.key, model.state.pending.single().key)
    }
    @Test fun `resolved original request cannot be replayed by stale retry confirmation`() = runTest {
        loadCertificate()
        val owner = session.state.value as SessionState.Active
        val certificate = (model.state.detail as ApiResult.Success).value
        var sends = 0
        gateway.onCertificateMutation = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        model.action(owner, certificate, CertificateAction.Delete); advanceUntilIdle()
        val pending = model.state.pending.single()
        journal.complete(pending)
        model.retry(owner, pending); advanceUntilIdle()
        assertEquals(1, sends)
        model.refresh(); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty())
        model.retry(owner, pending); advanceUntilIdle()
        assertEquals(1, sends)
    }
    @Test fun `editing a new draft pauses operation observation`() = runTest {
        login(); runCurrent()
        val receipt = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
        var reads = 0
        gateway.onCertificateOperation = { reads++; ApiResult.Success(receipt) }
        model.recover(receipt.operationId); advanceUntilIdle()
        model.create(true); model.observeOperation { true }; advanceUntilIdle()
        assertEquals(1, reads); assertNotNull(model.state.draft)
        gateway.onCertificateOperation = { reads++; ApiResult.Success(receipt.copy(state = CertificateOperationState.Succeeded)) }
        model.closeDraft(); model.observeOperation { true }; advanceUntilIdle()
        assertEquals(2, reads)
    }
    @Test fun `reopened cancellation rejects old object and each confirmation sends once`() = runTest {
        login(); runCurrent()
        val operation = CertificateWire.operation(CERTIFICATE_OPERATION_JSON).copy(state = CertificateOperationState.Running)
        gateway.onCertificateOperation = { ApiResult.Success(operation) }
        model.recover(operation.operationId); advanceUntilIdle()
        val owner = session.state.value as SessionState.Active
        model.requestCancellation(owner, operation)
        val old = requireNotNull(model.cancellation)
        model.dismissCancellation(); model.requestCancellation(owner, operation)
        val current = requireNotNull(model.cancellation)
        var sends = 0
        gateway.onCertificateCancel = { sends++; ApiResult.Transport(null) }
        model.cancel(old); assertSame(current, model.cancellation)
        model.cancel(current); model.cancel(current); advanceUntilIdle()
        assertEquals(1, sends); assertFalse(model.state.operationVerified)
    }
    @Test fun `refresh rereads known operation and revokes old cancellation`() = runTest {
        login(); runCurrent()
        val operation = CertificateWire.operation(CERTIFICATE_OPERATION_JSON).copy(state = CertificateOperationState.Running)
        gateway.onCertificateOperation = { ApiResult.Success(operation) }
        gateway.onCertificates = { ApiResult.Success(emptyList()) }
        model.recover(operation.operationId); advanceUntilIdle()
        model.requestCancellation(session.state.value as SessionState.Active, operation)
        val old = requireNotNull(model.cancellation)
        var reads = 0; var sends = 0
        gateway.onCertificateOperation = { reads++; ApiResult.Transport(null) }
        gateway.onCertificateCancel = { sends++; ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle(); model.cancel(old); advanceUntilIdle()
        assertEquals(1, reads); assertEquals(0, sends); assertNull(model.cancellation)
        assertFalse(model.state.operationVerified)
    }
    @Test fun `delayed observation rechecks foreground and stops during confirmation`() = runTest {
        login(); runCurrent()
        val operation = CertificateWire.operation(CERTIFICATE_OPERATION_JSON).copy(state = CertificateOperationState.Running)
        gateway.onCertificateOperation = { ApiResult.Success(operation) }
        model.recover(operation.operationId); advanceUntilIdle()
        var reads = 0; var foreground = true
        gateway.onCertificateOperation = { reads++; ApiResult.Success(operation) }
        val observer = launch { model.observeOperation { foreground } }; runCurrent()
        foreground = false; advanceTimeBy(1600); runCurrent(); observer.join()
        assertEquals(0, reads)
        model.requestCancellation(session.state.value as SessionState.Active, operation)
        model.observeOperation { true }; advanceUntilIdle()
        assertEquals(0, reads); assertNotNull(model.cancellation)
    }
    @Test fun `one observation follows identical running replies through terminal state`() = runTest {
        login(); runCurrent()
        val receipt = CertificateWire.operation(CERTIFICATE_OPERATION_JSON).copy(state = CertificateOperationState.Running)
        gateway.onCertificateOperation = { ApiResult.Success(receipt) }
        model.recover(receipt.operationId); advanceUntilIdle()
        var reads = 0
        gateway.onCertificateOperation = {
            reads++
            ApiResult.Success(if (reads < 4) receipt else receipt.copy(state = CertificateOperationState.Succeeded))
        }
        model.observeOperation { true }; advanceUntilIdle()
        assertEquals(4, reads)
        assertEquals(CertificateOperationState.Succeeded, model.state.operation?.state)
    }
}
