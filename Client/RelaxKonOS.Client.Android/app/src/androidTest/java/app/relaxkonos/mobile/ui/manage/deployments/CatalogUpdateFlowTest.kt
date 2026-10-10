package app.relaxkonos.mobile.ui.manage.deployments

import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class CatalogUpdateFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val baseline = DeploymentApplication("11111111-1111-1111-1111-111111111111", "review-app", "image", "web", "running", "running", "http", 1,
        "review-container", 8080, 9080, "127.0.0.1", null, null, null, "personal-site", "1.0.0", "/ready", DeploymentLimits(1.0, 16777217, 512),
        emptyList(), emptyList(), "2026-10-01T00:00:00.1234567+00:00")
    private val target = CatalogTemplate("1", "personal-site", "2.0.0", "RelaxKonOS", "built-in", true,
        "website", "review", listOf("linux/amd64"), emptyList(), CatalogResources(null, null, null), emptyList(), emptyList(), 80, "/", "retain data", false)
    private fun change(version: String) = CatalogApplicationUpdatePreview(baseline.id, baseline.updatedAt, null, "1.0.0",
        target.copy(version = version), "image:1", "image:2", "review-notes", emptyList())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: CatalogUpdateEditor
    private var reads = 0
    private var sends = 0
    private var accepted = 0
    private var closes = 0
    private var onSnapshot: suspend () -> ApiResult<DeploymentSnapshot> = { ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)) }
    private var onSend: suspend () -> ApiResult<DeploymentOperation> = { ApiResult.Transport(null) }
    private val visible = mutableStateOf(true)
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun action(id: Int) = rule.onNode(hasClickAction() and hasText(text(id)))
    private fun confirm(version: String = "2.0.0") = rule.onNodeWithText(InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.catalog_update_confirm, version))
    private fun dismissFeedback() {
        rule.waitUntil(5_000) { rule.onAllNodes(hasClickAction() and hasText(text(R.string.common_dismiss))).fetchSemanticsNodes().size == 1 }
        action(R.string.common_dismiss).performClick(); rule.waitForIdle(); SystemClock.sleep(500)
    }
    private fun show() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS), privilegedOperations = true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = baseline.id))
            override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> {
                assertEquals(baseline.id, applicationId); reads++; return onSnapshot()
            }
            override suspend fun previewCatalogUpdate(serverUrl: String, accessToken: String, applicationId: String, templateVersion: String): ApiResult<CatalogApplicationUpdatePreview> {
                assertEquals(baseline.id, applicationId); return ApiResult.Success(change(templateVersion))
            }
            override suspend fun updateCatalogApplication(serverUrl: String, accessToken: String, preview: CatalogApplicationUpdatePreview, idempotencyKey: String): ApiResult<DeploymentOperation> {
                assertEquals("2.0.0", preview.target.version); sends++; return onSend()
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://catalog-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        rule.runOnUiThread { editor = CatalogUpdateEditor(session, DeploymentRepository(gateway, session), owner, baseline, listOf(target, target.copy(version = "3.0.0")), scope) }
        rule.setContent { MaterialTheme {
            if (visible.value) CatalogUpdateContent(editor, owner, DeploymentRuntime(true, "", "review", "linux", "amd64"),
                { closes++; visible.value = false; editor.close() }, { accepted++ })
            else Text("Parent page")
        } }
        rule.waitUntil(5_000) { editor.preview is ApiResult.Success }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }

    @Test fun repeatedReadFailuresRemainVisibleAndPreserveExactTargetWithoutSubmitting() {
        show()
        rule.onNodeWithText("3.0.0").performClick()
        rule.waitUntil(5_000) { (editor.preview as? ApiResult.Success)?.value?.target?.version == "3.0.0" }
        confirm("3.0.0").assertIsEnabled()
        rule.runOnIdle { onSnapshot = { ApiResult.Transport(null) } }
        repeat(2) {
            action(R.string.deployments_revision_read_current).performScrollTo().performClick()
            rule.waitUntil(5_000) { !editor.busy && reads == it + 1 }
            dismissFeedback()
            rule.runOnIdle { assertEquals("3.0.0", editor.selectedVersion); assertEquals(baseline, editor.baseline); assertEquals(0, sends); assertEquals(0, accepted) }
        }
    }

    @Test fun unknownUpdateKeepsTargetAndBusyReadCannotCloseOrReplayIt() {
        show()
        rule.runOnIdle { onSend = { throw IllegalStateException("private send detail") } }
        confirm().performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.result != null }
        dismissFeedback()
        confirm().assertIsNotEnabled()
        rule.onNodeWithText("3.0.0").assertIsNotEnabled()
        rule.runOnIdle { assertTrue(editor.unknown); assertEquals(1, sends); assertEquals(0, accepted); onSnapshot = { ApiResult.Transport(null) } }
        action(R.string.deployments_revision_read_current).performScrollTo().performClick()
        rule.waitUntil(5_000) { !editor.busy }
        dismissFeedback()
        val current = CompletableDeferred<ApiResult<DeploymentSnapshot>>()
        rule.runOnIdle { onSnapshot = { current.await() } }
        action(R.string.deployments_revision_read_current).performClick()
        rule.waitUntil(5_000) { editor.busy }
        Espresso.pressBack()
        action(R.string.common_close).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(0, closes); assertEquals(1, sends) }
        current.complete(ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)))
        rule.waitUntil(5_000) { !editor.busy && !editor.unknown && editor.preview is ApiResult.Success }
        confirm().assertIsEnabled()
        rule.runOnIdle { assertEquals("2.0.0", editor.selectedVersion); assertEquals(1, sends); assertEquals(0, accepted) }
    }
}
