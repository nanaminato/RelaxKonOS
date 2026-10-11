package app.relaxkonos.mobile.ui.manage.firewall

import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class FirewallCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: FirewallViewModel
    private var cancelRead = false
    private var writes = 0
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val facts = FirewallFacts(FirewallStatus(true, false, "ufw", null, "deny", "allow", ""), emptyList())
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.FIREWALL), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun firewallStatus(serverUrl: String, accessToken: String): ApiResult<FirewallStatus> {
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(facts.status)
            }
            override suspend fun firewallRules(serverUrl: String, accessToken: String): ApiResult<List<FirewallRule>> = ApiResult.Success(emptyList())
            override suspend fun changeFirewall(serverUrl: String, accessToken: String, change: FirewallChange): ApiResult<FirewallResult> {
                writes++; throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://firewall-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = FirewallViewModel(session, FirewallRepository(gateway, session, elevation, FirewallMutationJournal(Storage())), ElevationAnswerProvider.Declines)
            store.put("firewall", model)
        }
        rule.setContent { MaterialTheme { FirewallContent(model, owner, {}) } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.firewall_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.firewall_enable)).assertDoesNotExist()
    }
    @Test fun cancelledRefreshRemovesOldActionsAndExplicitRefreshRecovers() {
        show()
        rule.runOnIdle { cancelRead = true }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        dismissFailure()
        rule.runOnIdle { cancelRead = false }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledChangeRetainsMarkerAndRefreshDoesNotReplayWrite() {
        show()
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.firewall_submit))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
}

