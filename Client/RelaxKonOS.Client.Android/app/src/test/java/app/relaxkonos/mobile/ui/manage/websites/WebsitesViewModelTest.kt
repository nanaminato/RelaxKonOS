package app.relaxkonos.mobile.ui.manage.websites

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class WebsitesViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        var readFailure = false
        var writeFailure = false
        override fun read(): ByteArray? { check(!readFailure) { "private storage failure" }; return bytes }
        override fun write(bytes: ByteArray) { check(!writeFailure); this.bytes = bytes.copyOf() }
    }
    private val delegate = FakeGateway()
    private var failure: Exception? = null
    private var reads = 0
    private var historyRead: suspend () -> ApiResult<List<WebsitePublicationOperation>> = { ApiResult.Success(emptyList()) }
    private var publicationRead: suspend () -> ApiResult<WebsitePublicationOperation> = { error("Unexpected publication read") }
    private val gateway = object : RelaxKonGateway by delegate {
        override suspend fun websitePublication(serverUrl: String, accessToken: String, operationId: String) = publicationRead()
        override suspend fun websitePublicationHistory(serverUrl: String, accessToken: String, applicationId: String) = historyRead()
        override suspend fun webServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> {
            reads++
            failure?.let { throw it }
            return ApiResult.Success(emptyList())
        }
    }
    private val session = AuthSession(gateway)
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val index = OperationIndex(object : OperationIndexStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val publicationStorage = Storage()
    private val journal = WebsitePublicationJournal(publicationStorage)
    private val store = ViewModelStore()
    private lateinit var model: WebsitesViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = WebsitesViewModel(session, WebPublishingRepository(gateway, session, elevation, index),
            DeploymentRepository(gateway, session), CertificateRepository(gateway, session, index, CertificateRequestJournal(Storage()), elevation), ElevationAnswerProvider.Declines, journal)
        store.put("websites", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        delegate.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.WEB_SERVER)))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://websites-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `cancelled refresh ends loading and permits explicit recovery`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertTrue(model.state.servers is ApiResult.Success)
        failure = CancellationException("private read failure")
        model.refresh(); advanceUntilIdle()
        assertFalse(model.state.loading); assertTrue(model.state.servers is ApiResult.Transport)
        assertNull(model.state.applications); assertNull(model.state.certificates)
        failure = null
        model.refresh(); advanceUntilIdle()
        assertFalse(model.state.loading); assertTrue(model.state.servers is ApiResult.Success)
        assertEquals(3, reads)
    }
    @Test fun `read exception ends loading without leaking exception text`() = runTest {
        login(); runCurrent()
        failure = IllegalStateException("private read failure")
        model.refresh(); advanceUntilIdle()
        assertFalse(model.state.loading)
        assertEquals(ApiResult.Transport(null), model.state.servers)
        failure = null; model.refresh(); advanceUntilIdle()
        assertTrue(model.state.servers is ApiResult.Success)
    }
    @Test fun `late operation read does not replace another application even after switching back`() = runTest {
        login(); runCurrent()
        model.selectApplication("application-a"); advanceUntilIdle()
        val reply = CompletableDeferred<ApiResult<WebsitePublicationOperation>>()
        publicationRead = { reply.await() }
        model.refreshPublication("operation-a"); runCurrent()
        model.selectApplication("application-b")
        model.selectApplication("application-a")
        reply.complete(ApiResult.Success(WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "succeeded", "complete", "", null, emptyList(), null, null, null)))
        advanceUntilIdle()
        assertEquals("application-a", model.state.selectedApplicationId)
        assertNull(model.state.publication)
    }
    @Test fun `cancelled operation read keeps original lookup target and can recover`() = runTest {
        login(); runCurrent()
        val operation = WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "succeeded", "complete", "", null, emptyList(), null, null, null)
        publicationRead = { ApiResult.Success(operation) }
        model.refreshPublication("operation-a"); advanceUntilIdle()
        publicationRead = { throw CancellationException("private read failure") }
        model.refreshPublication("operation-a"); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.publication)
        assertEquals("operation-a", model.state.publicationLookupId)
        assertFalse(model.state.publicationLoading)
        publicationRead = { ApiResult.Success(operation) }
        model.refreshPublication(requireNotNull(model.state.publicationLookupId)); advanceUntilIdle()
        assertEquals(ApiResult.Success(operation), model.state.publication)
    }
    @Test fun `different operation response is rejected before adding recovery reference`() = runTest {
        login(); runCurrent()
        publicationRead = { ApiResult.Success(WebsitePublicationOperation("operation-b", "application-a", "server", "example.test",
            null, null, null, "succeeded", "complete", "", null, emptyList(), null, null, null)) }
        model.refreshPublication("operation-a"); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.publication)
        assertEquals("operation-a", model.state.publicationLookupId)
        assertTrue(index.forOwner(session.state.value as SessionState.Active).isEmpty())
    }
    @Test fun `history exception becomes sanitized failure and permits recovery`() = runTest {
        login(); runCurrent()
        historyRead = { throw IllegalStateException("private history failure") }
        model.selectApplication("application-a"); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.publicationHistory)
        historyRead = { ApiResult.Success(emptyList()) }
        model.selectApplication("application-a"); advanceUntilIdle()
        assertEquals(ApiResult.Success(emptyList<WebsitePublicationOperation>()), model.state.publicationHistory)
    }
    @Test fun `history cancellation becomes sanitized failure`() = runTest {
        login(); runCurrent()
        historyRead = { throw CancellationException("private history failure") }
        model.selectApplication("application-a"); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.publicationHistory)
    }
    @Test fun `late history cannot replace newer read after switching back`() = runTest {
        login(); runCurrent()
        val reply = CompletableDeferred<ApiResult<List<WebsitePublicationOperation>>>()
        historyRead = { reply.await() }
        model.selectApplication("application-a"); runCurrent()
        val newer = CompletableDeferred<ApiResult<List<WebsitePublicationOperation>>>()
        historyRead = { newer.await() }
        model.selectApplication("application-b"); model.selectApplication("application-a")
        reply.complete(ApiResult.Transport(null)); runCurrent()
        assertNull(model.state.publicationHistory)
        newer.complete(ApiResult.Success(emptyList())); advanceUntilIdle()
        assertEquals(ApiResult.Success(emptyList<WebsitePublicationOperation>()), model.state.publicationHistory)
    }
    @Test fun `prerequisite refresh removing application clears its publication records`() = runTest {
        login(); runCurrent()
        model.selectApplication("application-a"); advanceUntilIdle()
        publicationRead = { ApiResult.Success(WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "succeeded", "complete", "", null, emptyList(), null, null, null)) }
        model.refreshPublication("operation-a"); advanceUntilIdle()
        assertNotNull(model.state.publicationHistory)
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.selectedApplicationId)
        assertNull(model.state.publication)
        assertNull(model.state.publicationLookupId)
        assertNull(model.state.publicationHistory)
        assertFalse(model.state.historyLoading)
    }
    @Test fun `publication timer rechecks foreground before querying original id`() = runTest {
        login(); runCurrent()
        val operation = WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "running", "verifying", "", null, emptyList(), null, null, null)
        var queries = 0
        publicationRead = { queries++; ApiResult.Success(operation) }
        model.refreshPublication(operation.operationId); advanceUntilIdle()
        var foreground = true
        val observer = launch { model.observePublication(operation) { foreground } }; runCurrent()
        foreground = false; advanceTimeBy(1100); runCurrent(); observer.join()
        assertEquals(1, queries)
        model.observePublication(operation) { true }; advanceUntilIdle()
        assertEquals(2, queries)
    }
    @Test fun `stale running snapshot cannot restart terminal task observation`() = runTest {
        login(); runCurrent()
        val running = WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "running", "verifying", "", null, emptyList(), null, null, null)
        val terminal = running.copy(state = "succeeded")
        var queries = 0
        publicationRead = { queries++; ApiResult.Success(terminal) }
        model.refreshPublication(running.operationId); advanceUntilIdle()
        model.observePublication(running) { true }; advanceUntilIdle()
        assertEquals(1, queries); assertEquals(ApiResult.Success(terminal), model.state.publication)
    }
    @Test fun `manual task read invalidates older scheduled observation`() = runTest {
        login(); runCurrent()
        val running = WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "running", "verifying", "", null, emptyList(), null, null, null)
        var queries = 0
        publicationRead = { queries++; ApiResult.Success(running) }
        model.refreshPublication(running.operationId); advanceUntilIdle()
        val observer = launch { model.observePublication(running) { true } }; runCurrent()
        model.refreshPublication(running.operationId); runCurrent()
        advanceTimeBy(1100); runCurrent(); observer.join()
        assertEquals(2, queries)
    }
    @Test fun `delayed observer does not restore operation after changing application`() = runTest {
        login(); runCurrent()
        model.selectApplication("application-a"); advanceUntilIdle()
        val operation = WebsitePublicationOperation("operation-a", "application-a", "server", "example.test",
            null, null, null, "running", "verifying", "", null, emptyList(), null, null, null)
        publicationRead = { ApiResult.Success(operation) }
        model.refreshPublication(operation.operationId); advanceUntilIdle()
        launch { model.observePublication(operation) { true } }; runCurrent()
        model.selectApplication("application-b"); advanceUntilIdle()
        assertNull(model.state.publicationLookupId)
        assertNull(model.state.publication)
    }
    private val original = WebsitePublicationOperation("original-id", "application-a", "server", "example.test",
        null, null, null, "succeeded", "complete", "", null, emptyList(), null, null, null)
    private suspend fun TestScope.seedPending(): WebsitePublicationReview {
        login(); runCurrent()
        journal.begin(session.state.value as SessionState.Active,
            WebsitePublishRequest(original.applicationId, original.webServerId, original.domain, confirmed = true))
        model.refresh(); advanceUntilIdle()
        return requireNotNull(model.state.pendingPublication)
    }
    @Test fun `offline prerequisite failure still exposes durable pending publication`() = runTest {
        login(); runCurrent()
        journal.begin(session.state.value as SessionState.Active,
            WebsitePublishRequest(original.applicationId, original.webServerId, original.domain, confirmed = true))
        failure = IllegalStateException("offline")
        model.refresh(); advanceUntilIdle()
        assertNotNull(model.state.pendingPublication)
        assertEquals(ApiResult.Transport(null), model.state.servers)
    }
    @Test fun `known recovery receipt survives local read failure`() = runTest {
        val pending = seedPending()
        publicationRead = { publicationStorage.readFailure = true; ApiResult.Success(original) }
        model.recoverPendingPublication(pending, original.operationId); advanceUntilIdle()
        assertEquals(ApiResult.Success(original), model.state.publication)
        assertEquals(original.operationId, model.state.publicationLookupId)
        assertTrue(model.state.publicationStorageFailed)
        assertNotNull(model.state.pendingPublication)
        assertFalse(model.state.publishing)
    }
    @Test fun `local write failure is visible alongside authoritative receipt and explicit recovery clears it`() = runTest {
        val pending = seedPending()
        publicationRead = { ApiResult.Success(original) }
        publicationStorage.writeFailure = true
        model.recoverPendingPublication(pending, original.operationId); advanceUntilIdle()
        assertEquals(ApiResult.Success(original), model.state.publication)
        assertTrue(model.state.publicationStorageFailed)
        publicationStorage.writeFailure = false
        model.recoverPendingPublication(requireNotNull(model.state.pendingPublication), original.operationId); advanceUntilIdle()
        assertEquals(ApiResult.Success(original), model.state.publication)
        assertFalse(model.state.publicationStorageFailed)
        assertNull(model.state.pendingPublication)
    }
    @Test fun `old pending recovery callback cannot consume refreshed pending snapshot`() = runTest {
        val pending = seedPending()
        model.refresh(); advanceUntilIdle()
        var queries = 0
        publicationRead = { queries++; ApiResult.Success(original) }
        model.recoverPendingPublication(pending, original.operationId); advanceUntilIdle()
        assertEquals(0, queries)
        assertNotNull(model.state.pendingPublication)
    }

}
