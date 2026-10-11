package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class HostSettingsFeedbackFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: HostSettingsEditor
    private var cancelRead = false
    private var writes = 0
    private var queries = 0
    private var backNavigations = 0
    private var httpsNavigations = 0
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private val target = HostSettingsTarget("host/time", "hostMachine", null)
    private val revision = "A".repeat(64)
    private val facts = HostTimeSettings("UTC", listOf("UTC", "Asia/Tokyo"), revision,
        "2026-10-01T00:00:00Z", "review", target, HostSettingsCapability("available", null), "immediate")
    private val plan = HostSettingsPlan("ca57b835-8242-47bb-8a2b-c3a5f4658a6b", target, revision,
        "2099-10-01T00:00:00Z", listOf(HostSettingsDifference("host.time.zone", "UTC", "Asia/Tokyo")),
        "hostTimeChange", "host/time", "immediate", "")
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.CERTIFICATES), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun hostTime(serverUrl: String, accessToken: String): ApiResult<HostTimeSettings> {
                if (cancelRead) throw CancellationException("private detail")
                return ApiResult.Success(facts)
            }
            override suspend fun previewHostSettings(serverUrl: String, accessToken: String, kind: HostSettingKind,
                expectedRevision: String, key: String, value: String?, scope: HostEnvironmentScope?,
                mutation: HostEnvironmentMutation?, confirmHighImpact: Boolean) = ApiResult.Success(plan)
            override suspend fun applyHostSettings(serverUrl: String, accessToken: String, kind: HostSettingKind,
                planId: String): ApiResult<HostSettingsOperation> {
                writes++; throw CancellationException("private detail")
            }
            override suspend fun hostSettingsOperation(serverUrl: String, accessToken: String, id: String): ApiResult<HostSettingsOperation> {
                queries++; assertEquals(plan.id, id)
                return ApiResult.Success(HostSettingsOperation(id, "host.time.zone", target, "applied",
                    "2026-10-01T00:00:00Z", revision, null, "immediate"))
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://settings-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val journal = HostSettingsJournal(object : InstallationRequestStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val repository = HostSettingsRepository(gateway, session,
            ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage())), journal)
        rule.runOnUiThread {
            editor = HostSettingsEditor(owner, repository, { session.state.value === owner }, scope, ElevationAnswerProvider.Declines)
            editor.lifecycle(true)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) {
                    HostSettingsContent(editor, { backNavigations++ }, { httpsNavigations++ })
                }
            }
        } }
        rule.waitUntil(5_000) { editor.time != null && !editor.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { if (::editor.isInitialized) editor.close(); scope.cancel() } }
    private fun button(id: Int) = rule.onNode(hasText(text(id)) and hasClickAction())
    private fun acknowledge() {
        button(R.string.common_dismiss).performClick()
        rule.waitUntil(5_000) { rule.onAllNodesWithText(text(R.string.common_dismiss)).fetchSemanticsNodes().isEmpty() }
    }
    @Test fun discardConfirmationKeepsTheRequestedHttpsDestination() {
        show()
        rule.onNode(hasSetTextAction()).performScrollTo().performTextReplacement("Asia/Tokyo")
        button(R.string.server_https_title).performScrollTo().performClick()
        rule.waitUntil(5_000) { editor.leaveReview }
        button(R.string.common_cancel).performClick()
        rule.runOnIdle {
            assertEquals("Asia/Tokyo", editor.value)
            assertEquals(0, backNavigations); assertEquals(0, httpsNavigations)
        }
        button(R.string.server_https_title).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.host_settings_discard)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.runOnIdle {
            assertEquals(1, httpsNavigations); assertEquals(0, backNavigations)
            assertFalse(editor.dirty); assertEquals(0, writes)
        }
    }
    @Test fun cancelledRefreshClearsOldFactsAndRefreshCanRecover() {
        show()
        rule.runOnIdle { cancelRead = true }
        button(R.string.common_refresh).performScrollTo().performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.error != null }
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        acknowledge()
        rule.runOnIdle { assertNull(editor.time); assertFalse(editor.canPreview); cancelRead = false }
        button(R.string.common_refresh).performScrollTo().performClick()
        rule.waitUntil(5_000) { editor.time != null && !editor.busy }
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledApplyProvidesOriginalQueryAndDoesNotResubmit() {
        show()
        rule.onNode(hasSetTextAction()).performScrollTo().performTextReplacement("Asia/Tokyo")
        button(R.string.host_settings_preview).performScrollTo().performClick()
        rule.waitUntil(5_000) { editor.plan != null }
        button(R.string.host_settings_apply).assertIsDisplayed().performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.error != null }
        rule.onNodeWithText(text(R.string.host_settings_unknown)).assertIsDisplayed()
        rule.onNodeWithText("private detail", substring = true).assertDoesNotExist()
        acknowledge()
        button(R.string.host_settings_preview).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(plan.id).performScrollTo().assertIsDisplayed()
        button(R.string.host_settings_query).performScrollTo().performClick()
        rule.waitUntil(5_000) { queries == 1 && !editor.busy }
        rule.runOnIdle { assertEquals(1, writes); assertFalse(editor.references.single().unresolved) }
    }
}
