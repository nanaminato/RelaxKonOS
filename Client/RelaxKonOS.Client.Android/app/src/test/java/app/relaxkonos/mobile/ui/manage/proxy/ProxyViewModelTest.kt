package app.relaxkonos.mobile.ui.manage.proxy

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
class ProxyViewModelTest {
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
    private val repository = ProxyRepository(gateway, session, index, ProxyRequestJournal(Storage()))
    private val installations = InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage()))
    private val store = ViewModelStore()
    private lateinit var model: ProxyViewModel
    private val operation = ProxyOperation("55555555-5555-5555-5555-555555555555", "start", ProxyOperationState.Running, "starting", "")
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Mihomo,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private val overview = ProxyOverview(ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
        null, true, true, "healthy", "", false, true, false, "linux", false, ProxySystemProxyCapabilities(true, false, true, false), supportsLogs = true)
    private val sent = mutableListOf<String>()
    private var operationReads = 0
    private var installationReads = 0
    private var logReads = 0
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = ProxyViewModel(session, repository, installations, index, ElevationAnswerProvider.Declines, UploadDocumentOpener { null })
        store.put("proxy", model)
        gateway.onProxyOverview = { ApiResult.Success(overview) }
        gateway.onProxyProfiles = { ApiResult.Success(emptyList()) }
        gateway.onProxySubscriptions = { ApiResult.Success(emptyList()) }
        gateway.onProxyRouting = { ApiResult.Success(ProxyRoutingMode.Rule) }
        gateway.onProxyDownloadOptions = { ApiResult.Success(false) }
        gateway.onProxySettings = { ApiResult.Success(ProxySettings(false, false, true, false, false, "info", 7890, false, "127.0.0.1", null, null)) }
        gateway.onProxyRecovery = { ApiResult.Success(ProxyRecovery(false, false, null, "")) }
        gateway.onProxyGeoData = { ApiResult.Success(ProxyGeoData(false, null)) }
        gateway.onActiveInstallation = { ApiResult.Success(install) }
        gateway.onInstallation = { installationReads++; ApiResult.Success(install) }
        gateway.onProxyOperation = { operationReads++; ApiResult.Success(operation) }
        gateway.onCancelInstallation = { id, _ -> sent += id; ApiResult.Transport(null) }
        gateway.onProxyLogs = { logReads++; ApiResult.Success(listOf(ProxyLog(1, "info", "review"))) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.PROXY), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private suspend fun TestScope.ready(): SessionState.Active {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.recoverOperation(operation.operationId); advanceUntilIdle()
        return owner
    }
    @Test fun `replacement installation rejects old callback and current confirmation sends once`() = runTest {
        val owner = ready()
        model.requestInstallationCancellation(owner, install)
        val old = requireNotNull(model.installationCancellation)
        val replacement = install.copy(operationId = "66666666-6666-6666-6666-666666666666")
        gateway.onActiveInstallation = { ApiResult.Success(replacement) }
        model.refresh(); advanceUntilIdle(); model.cancelInstall(old); advanceUntilIdle()
        assertNull(model.installationCancellation); assertTrue(sent.isEmpty())
        model.requestInstallationCancellation(owner, replacement)
        val current = requireNotNull(model.installationCancellation)
        model.cancelInstall(current); model.cancelInstall(current); advanceUntilIdle()
        assertEquals(listOf(replacement.operationId), sent); assertFalse(model.state.installationVerified)
    }
    @Test fun `reopening same task cannot revive dismissed confirmation`() = runTest {
        val owner = ready()
        model.requestInstallationCancellation(owner, install)
        val old = requireNotNull(model.installationCancellation)
        model.dismissInstallationCancellation(); model.requestInstallationCancellation(owner, install)
        val current = requireNotNull(model.installationCancellation)
        model.cancelInstall(old)
        assertSame(current, model.installationCancellation); assertTrue(sent.isEmpty())
    }
    @Test fun `same address new login clears tasks and rejects old confirmation`() = runTest {
        val owner = ready(); model.requestInstallationCancellation(owner, install)
        val old = requireNotNull(model.installationCancellation)
        login(); runCurrent(); model.cancelInstall(old); advanceUntilIdle()
        assertNull(model.state.installation); assertNull(model.installationCancellation); assertTrue(sent.isEmpty())
    }
    @Test fun `one foreground observation reads both active task types`() = runTest {
        ready(); val webBefore = operationReads; val installBefore = installationReads
        model.observeTasks { true }; advanceUntilIdle()
        assertEquals(webBefore + 1, operationReads); assertEquals(installBefore + 1, installationReads)
    }
    @Test fun `delayed observation rechecks foreground and manual read generation`() = runTest {
        ready(); val before = operationReads
        var visible = true
        val observation = launch { model.observeTasks { visible } }; runCurrent()
        visible = false; advanceTimeBy(1600); runCurrent(); observation.join()
        assertEquals(before, operationReads)
        val old = launch { model.observeTasks { true } }; runCurrent()
        model.pollInstall(); runCurrent(); val after = installationReads
        advanceTimeBy(1600); runCurrent(); old.join()
        assertEquals(after, installationReads); assertEquals(before, operationReads)
    }
    @Test fun `cancelled refresh clears old facts and cannot retain verified installation`() = runTest {
        ready(); gateway.onProxyProfiles = { throw CancellationException("isolated cancellation") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.overview); assertNull(model.state.profiles); assertNull(model.state.observedAtMillis)
        assertFalse(model.state.installationVerified); assertFalse(model.state.busy)
    }
    @Test fun `stopped diagnostics cannot publish late sample or clear newer busy state`() = runTest {
        ready()
        val old = CompletableDeferred<ApiResult<List<ProxyLog>>>()
        val fresh = CompletableDeferred<ApiResult<List<ProxyLog>>>()
        gateway.onProxyLogs = { withContext(NonCancellable) { old.await() } }
        model.diagnostics("logs"); runCurrent(); assertTrue(model.state.diagnosticsBusy)
        model.stopDiagnostics()
        gateway.onProxyLogs = { fresh.await() }
        model.diagnostics("logs"); runCurrent()
        old.complete(ApiResult.Success(listOf(ProxyLog(1, "info", "late")))); runCurrent()
        assertNull(model.state.logs); assertTrue(model.state.diagnosticsBusy)
        fresh.complete(ApiResult.Success(listOf(ProxyLog(2, "info", "fresh")))); advanceUntilIdle()
        assertEquals("fresh", (model.state.logs as ApiResult.Success).value.single().message)
        assertFalse(model.state.diagnosticsBusy)
    }
    @Test fun `diagnostic failure retries only while original page remains foreground`() = runTest {
        ready(); gateway.onProxyLogs = { logReads++; ApiResult.Transport(null) }
        var visible = true
        val observing = launch { model.observeDiagnostics("logs") { visible } }
        runCurrent(); advanceTimeBy(6500); runCurrent()
        assertEquals(3, logReads); assertTrue(model.state.diagnosticsFailed)
        visible = false; advanceTimeBy(3500); runCurrent(); observing.join()
        assertEquals(3, logReads)
        gateway.onProxyLogs = { logReads++; ApiResult.Success(emptyList()) }
        model.diagnostics("logs"); advanceUntilIdle()
        assertEquals(4, logReads); assertFalse(model.state.diagnosticsFailed)
    }
}
