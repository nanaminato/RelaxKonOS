package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

@OptIn(ExperimentalCoroutinesApi::class)
class ProxyServerViewModelTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val index = OperationIndex(object : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val repository = ProxyServerRepository(gateway, session, index)
    private val store = ViewModelStore()
    private lateinit var model: ProxyServerViewModel
    private val a = "11111111-1111-1111-1111-111111111111"
    private val b = "22222222-2222-2222-2222-222222222222"
    private val uri = listOf(ProxyServerExportOption("uri", true, null))
    private val json = listOf(ProxyServerExportOption("sing-box", true, null))
    private val snapshot = ProxyServerSnapshot(ProxyServerConfiguration(1, emptyList(), null), emptyList(),
        listOf(a, b).map { ProxyServerCredential(it, "principal", it, emptyList(), true, null, true) }, emptyList(),
        ProxyServerStatus(true, true, true, false, "1", null, true, "", false, null),
        ProxyServerCapabilities(true, "linux", "amd64", "1", emptyList(), false, false, true, true, true, true), 1, null)
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = ProxyServerViewModel(session, repository, index); store.put("proxy-server", model)
        gateway.onProxyServerSnapshot = { ApiResult.Success(snapshot) }
        gateway.onProxyServerExportFormats = { ApiResult.Success(uri) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun TestScope.start() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.PROXY_SERVER), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://proxy-server-review.invalid"), "review", "synthetic".toCharArray()) {}
        runCurrent(); model.refresh(); advanceUntilIdle()
    }
    private fun select(id: String) { model.selectCredential(id); model.loadExportFormats(id) }

    @Test fun `late format response cannot replace another credential options`() = runTest {
        start(); lateinit var old: Continuation<ApiResult<List<ProxyServerExportOption>>>
        val calls = mutableListOf<String>()
        gateway.onProxyServerExportFormats = { id -> calls += id; if (id == a) suspendCoroutine<ApiResult<List<ProxyServerExportOption>>> { old = it } else ApiResult.Success(json) }
        select(a); runCurrent(); select(b); runCurrent()
        old.resume(ApiResult.Success(uri)); advanceUntilIdle()
        assertEquals(listOf(a, b), calls); assertEquals(b, model.selectedCredentialId)
        assertEquals(json, model.state.exportOptions); assertFalse(model.state.exporting)
    }
    @Test fun `clear during export prevents late secret from returning`() = runTest {
        start(); select(a); advanceUntilIdle()
        lateinit var reply: Continuation<ApiResult<ProxyServerExport>>
        gateway.onProxyServerExport = { _, _ -> suspendCoroutine { reply = it } }
        model.export(session.state.value as SessionState.Active, model.disclosureVersion, "uri"); runCurrent(); assertTrue(model.state.busy)
        model.clearExport(); reply.resume(ApiResult.Success(ProxyServerExport("uri", "synthetic-secret", null)))
        advanceUntilIdle(); assertNull(model.state.export); assertFalse(model.state.busy)
    }
    @Test fun `selecting another credential withdraws in-flight secret`() = runTest {
        start(); select(a); advanceUntilIdle()
        lateinit var reply: Continuation<ApiResult<ProxyServerExport>>
        gateway.onProxyServerExport = { _, _ -> suspendCoroutine { reply = it } }
        model.export(session.state.value as SessionState.Active, model.disclosureVersion, "uri"); runCurrent(); select(b); runCurrent()
        reply.resume(ApiResult.Success(ProxyServerExport("uri", "synthetic-secret-for-a", null)))
        advanceUntilIdle(); assertNull(model.state.export); assertEquals(b, model.selectedCredentialId)
    }
    @Test fun `clearing issued token during submission prevents late redisclosure`() = runTest {
        start(); lateinit var reply: Continuation<ApiResult<ProxyServerSubscriptionIssued>>
        gateway.onProxyServerIssueSubscription = { _, _ -> suspendCoroutine { reply = it } }
        model.issueSubscription(session.state.value as SessionState.Active, model.disclosureVersion, a, listOf("uri"), null); runCurrent(); assertTrue(model.state.busy)
        model.clearIssued()
        val subscription = ProxyServerSubscription("33333333-3333-3333-3333-333333333333", a, listOf("uri"), 1, null, false)
        reply.resume(ApiResult.Success(ProxyServerSubscriptionIssued(subscription, "synthetic-once-token", emptyList())))
        advanceUntilIdle(); assertNull(model.state.issued); assertFalse(model.state.busy)
    }
    @Test fun `unexpected format failure releases loading and exposes a safe failure`() = runTest {
        start(); gateway.onProxyServerExportFormats = { error("private format detail") }
        select(a); advanceUntilIdle()
        assertFalse(model.state.exporting); assertTrue(model.state.uncertain); assertTrue(model.state.exportOptions.isEmpty())
    }
    @Test fun `leaving the section prevents late export and requires reselection`() = runTest {
        start(); select(a); advanceUntilIdle()
        lateinit var reply: Continuation<ApiResult<ProxyServerExport>>
        gateway.onProxyServerExport = { _, _ -> suspendCoroutine { reply = it } }
        model.export(session.state.value as SessionState.Active, model.disclosureVersion, "uri"); runCurrent()
        model.clearSensitiveOutputs()
        reply.resume(ApiResult.Success(ProxyServerExport("uri", "synthetic-late", null)))
        advanceUntilIdle()
        assertNull(model.state.export); assertNull(model.selectedCredentialId)
        assertTrue(model.state.exportOptions.isEmpty()); assertFalse(model.state.exporting)
    }
    @Test fun `old subscription callback cannot issue after a disclosure boundary`() = runTest {
        start(); val owner = session.state.value as SessionState.Active
        val version = model.disclosureVersion
        var calls = 0
        gateway.onProxyServerIssueSubscription = { _, _ -> calls++; ApiResult.Transport(null) }
        model.clearSensitiveOutputs()
        model.issueSubscription(owner, version, a, listOf("uri"), null)
        advanceUntilIdle(); assertEquals(0, calls); assertNull(model.state.issued)
    }
    @Test fun `current export is displayed and explicit clear removes it`() = runTest {
        start(); select(a); advanceUntilIdle()
        gateway.onProxyServerExport = { _, _ -> ApiResult.Success(ProxyServerExport("uri", "synthetic-current", null)) }
        model.export(session.state.value as SessionState.Active, model.disclosureVersion, "uri")
        advanceUntilIdle(); assertEquals("synthetic-current", model.state.export?.content)
        model.clearExport(); assertNull(model.state.export)
    }

}
