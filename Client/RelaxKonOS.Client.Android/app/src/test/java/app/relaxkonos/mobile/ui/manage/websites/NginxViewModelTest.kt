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
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class NginxViewModelTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val index = OperationIndex(object : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val installations = InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage()))
    private val webServers = WebServerRepository(gateway, session, elevation, index, WebServerRequestJournal(Storage()))
    private val store = ViewModelStore()
    private lateinit var model: NginxViewModel
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Nginx,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private val web = WebServerOperation("55555555-5555-5555-5555-555555555555", "review-instance", "start",
        WebServerOperationState.Running, "starting", "", null, 1, null)
    private val sent = mutableListOf<String>()
    private var reads = 0
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = NginxViewModel(session, gateway, webServers, WebPublishingRepository(gateway, session, elevation, index),
            WebSiteRepository(gateway, session, elevation, WebSiteMutationJournal(Storage())),
            CertificateRepository(gateway, session, index, CertificateRequestJournal(Storage()), elevation),
            installations, index, ElevationAnswerProvider.Declines, UploadDocumentOpener { null })
        store.put("nginx", model)
        gateway.onHostOperatingSystem = { ApiResult.Success(HostOperatingSystemKind.Linux) }
        gateway.onActiveInstallation = { ApiResult.Success(install) }
        gateway.onInstallation = { reads++; ApiResult.Success(install) }
        gateway.onWebOperation = { reads++; ApiResult.Success(web) }
        gateway.onCancelInstallation = { id, _ -> sent += id; ApiResult.Transport(null) }
        gateway.onWebCancel = { id -> sent += id; ApiResult.Transport(null) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.WEB_SERVER), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private suspend fun TestScope.ready(): SessionState.Active {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.recover(web.operationId); advanceUntilIdle()
        return owner
    }
    @Test fun `replacement task revokes old installation cancellation and new confirmation sends once`() = runTest {
        val owner = ready()
        val old = NginxCancellation.Installation(owner, install)
        model.requestCancellation(old)
        val replacement = install.copy(operationId = "66666666-6666-6666-6666-666666666666")
        gateway.onActiveInstallation = { ApiResult.Success(replacement) }
        model.refresh(); advanceUntilIdle()
        model.cancel(old); advanceUntilIdle()
        assertNull(model.cancellation); assertTrue(sent.isEmpty())
        val current = NginxCancellation.Installation(owner, replacement)
        model.requestCancellation(current); model.cancel(current); model.cancel(current); advanceUntilIdle()
        assertEquals(listOf(replacement.operationId), sent)
        assertFalse(model.state.installationVerified)
    }
    @Test fun `reopening web cancellation cannot revive dismissed callback`() = runTest {
        val owner = ready()
        val old = NginxCancellation.Web(owner, web)
        model.requestCancellation(old); model.dismissCancellation()
        val current = NginxCancellation.Web(owner, web)
        model.requestCancellation(current); model.cancel(old)
        assertSame(current, model.cancellation); assertTrue(sent.isEmpty())
        model.cancel(current); model.cancel(current); advanceUntilIdle()
        assertEquals(listOf(web.operationId), sent); assertFalse(model.state.operationVerified)
    }
    @Test fun `failed refresh revokes cached task cancellation until explicit recovery`() = runTest {
        val owner = ready()
        gateway.onActiveInstallation = { ApiResult.Transport(null) }
        gateway.onWebOperation = { ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle()
        assertFalse(model.state.operationVerified); assertFalse(model.state.installationVerified)
        assertTrue(model.state.uncertain)
        model.requestCancellation(NginxCancellation.Web(owner, web))
        assertNull(model.cancellation)
        model.requestCancellation(NginxCancellation.Installation(owner, install))
        assertNull(model.cancellation); assertTrue(sent.isEmpty())
    }
    @Test fun `same address new login rejects old cancellation and resets tasks`() = runTest {
        val owner = ready()
        val old = NginxCancellation.Web(owner, web)
        model.requestCancellation(old)
        login(); runCurrent(); model.cancel(old); advanceUntilIdle()
        assertNull(model.cancellation); assertNull(model.state.operation); assertTrue(sent.isEmpty())
    }
    @Test fun `pending observation rechecks foreground and manual read generation`() = runTest {
        ready(); val before = reads
        var visible = true
        val observation = launch { model.observeTasks { visible } }; runCurrent()
        visible = false; advanceTimeBy(1600); runCurrent(); observation.join()
        assertEquals(before, reads)
        val old = launch { model.observeTasks { true } }; runCurrent()
        model.pollInstallation(); runCurrent(); val after = reads
        advanceTimeBy(1600); runCurrent(); old.join()
        assertEquals(after, reads)
    }
    @Test fun `confirmation blocks observation and close permits next foreground read`() = runTest {
        val owner = ready(); val before = reads
        model.requestCancellation(NginxCancellation.Installation(owner, install))
        model.observeTasks { true }; advanceUntilIdle()
        assertEquals(before, reads); assertNotNull(model.cancellation)
        model.dismissCancellation()
        model.observeTasks { true }; advanceUntilIdle()
        assertTrue(reads > before)
    }
    @Test fun `successful installation sample cannot hide failed web observation`() = runTest {
        ready()
        gateway.onWebOperation = { ApiResult.Problem(403, "webserver.forbidden", null) }
        model.pollTasks(); advanceUntilIdle()
        assertEquals("webserver.forbidden", model.state.problemCode)
        assertTrue(model.state.uncertain); assertFalse(model.state.operationVerified)
        assertTrue(model.state.installationVerified)
        val before = reads
        model.observeTasks { true }; advanceUntilIdle()
        assertEquals(before, reads)
    }
}
