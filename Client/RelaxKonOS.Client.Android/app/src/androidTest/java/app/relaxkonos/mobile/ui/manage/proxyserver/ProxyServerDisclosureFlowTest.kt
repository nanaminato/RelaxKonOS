package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*

class ProxyServerDisclosureFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ProxyServerViewModel
    private val reply = CompletableDeferred<ApiResult<ProxyServerExport>>()
    @Volatile private var exports = 0
    private val id = "11111111-1111-1111-1111-111111111111"
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun start() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val snapshot = ProxyServerSnapshot(ProxyServerConfiguration(1, emptyList(), null), emptyList(),
            listOf(ProxyServerCredential(id, "principal", "Review Alpha", emptyList(), true, null, true)), emptyList(),
            ProxyServerStatus(true, true, true, false, "1", null, true, "", false, null),
            ProxyServerCapabilities(true, "linux", "amd64", "1", emptyList(), false, false, true, true, true, true), 1, null)
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.PROXY_SERVER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = id))
            override suspend fun proxyServerSnapshot(serverUrl: String, accessToken: String) = ApiResult.Success(snapshot)
            override suspend fun proxyServerReleases(serverUrl: String, accessToken: String): ApiResult<List<ProxyServerRelease>> = ApiResult.Success(emptyList())
            override suspend fun proxyServerExportFormats(serverUrl: String, accessToken: String, credentialId: String) =
                ApiResult.Success(listOf(ProxyServerExportOption("uri", true, null)))
            override suspend fun proxyServerExport(serverUrl: String, accessToken: String, credentialId: String, request: ProxyServerExportRequest): ApiResult<ProxyServerExport> {
                exports++; return reply.await()
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://proxy-server-review.invalid"), "review", "synthetic".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread { model = ProxyServerViewModel(session, ProxyServerRepository(gateway, session, index), index); store.put("server", model) }
        rule.setContent { MaterialTheme { ProxyServerContent(model, owner, {}) } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
        tab(R.string.proxy_server_connect_title)
        rule.onNodeWithText("Review Alpha").performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.exportOptions.isNotEmpty() && !model.state.exporting }
        rule.onNode(hasText(text(R.string.proxy_server_format_uri)) and hasClickAction()).performScrollTo().performClick()
        rule.waitUntil(5_000) { exports == 1 }
    }
    private fun tab(id: Int) = rule.onAllNodesWithText(text(id)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
    @Test fun leavingConnectWithdrawsLateExportAndRequiresReselection() {
        start(); tab(R.string.workspace_overview)
        reply.complete(ApiResult.Success(ProxyServerExport("uri", "synthetic-late-secret", null)))
        rule.waitUntil(5_000) { !model.state.busy }
        tab(R.string.proxy_server_connect_title)
        rule.onNodeWithText("synthetic-late-secret").assertDoesNotExist()
        rule.runOnIdle { assertNull(model.selectedCredentialId); assertTrue(model.state.exportOptions.isEmpty()); assertEquals(1, exports) }
    }
    @Test fun pauseWithdrawsLateExportWithoutReplayingRequest() {
        start(); rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
        reply.complete(ApiResult.Success(ProxyServerExport("uri", "synthetic-late-secret", null)))
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(5_000) { !model.state.busy }
        rule.onNodeWithText("synthetic-late-secret").assertDoesNotExist()
        rule.runOnIdle { assertNull(model.selectedCredentialId); assertTrue(model.state.exportOptions.isEmpty()); assertEquals(1, exports) }
    }
}
