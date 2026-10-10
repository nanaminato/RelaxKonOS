package app.relaxkonos.mobile.ui.manage.deployments

import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DeploymentRevisionFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val baseline = DeploymentApplication("11111111-1111-1111-1111-111111111111", "review-app", "image", "web", "running", "running", "http", 1,
        "review-container", 8080, 9080, "127.0.0.1", null, null, null, null, null, "/ready", DeploymentLimits(1.0, 16777217, 512),
        emptyList(), emptyList(), "2026-10-01T00:00:00.1234567+00:00")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: DeploymentRevisionEditor
    private var reads = 0
    private var sends = 0
    private var clears = 0
    private var closes = 0
    private var accepted = 0
    private var onSnapshot: suspend (DeploymentApplication) -> ApiResult<DeploymentSnapshot> = { ApiResult.Success(DeploymentSnapshot(it, emptyList(), emptyList(), null)) }
    private var onSend: suspend () -> ApiResult<DeploymentOperation> = { ApiResult.Transport(null) }
    private val archive = mutableStateOf<ApiResult<DeploymentArchive>?>(null)
    private val staging = mutableStateOf(false)
    private val visible = mutableStateOf(true)
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun action(id: Int) = rule.onNode(hasClickAction() and hasText(text(id)))
    private fun guardAction(id: Int) = rule.onNode(hasClickAction() and hasText(text(id)) and
        hasAnyAncestor(isDialog() and hasAnyDescendant(hasText(text(R.string.ui_discard_draft_title)))))
    private fun dismissFeedback() {
        action(R.string.common_dismiss).performClick()
        rule.waitForIdle()
        // Compose idle does not wait for the platform dialog's fade-out/focus transfer.
        SystemClock.sleep(500)
    }
    private fun show(sourceKind: String = "image") {
        val actual = baseline.copy(sourceKind = sourceKind)
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS), privilegedOperations = true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = actual.id))
            override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> {
                assertEquals(actual.id, applicationId); reads++; return onSnapshot(actual)
            }
            override suspend fun deployRevision(serverUrl: String, accessToken: String, applicationId: String, source: DeploymentRevisionSource,
                expectedUpdatedAt: String, idempotencyKey: String): ApiResult<DeploymentOperation> {
                assertEquals(actual.id, applicationId); assertEquals(actual.updatedAt, expectedUpdatedAt); sends++; return onSend()
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://revision-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        rule.runOnUiThread {
            rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            editor = DeploymentRevisionEditor(session, DeploymentRepository(gateway, session), owner, DeploymentSnapshot(actual, emptyList(), emptyList(), null), scope)
        }
        val template = DeploymentTemplate(sourceKind, "Review", null, sourceKind != "image", sourceKind == "image", true, 8080)
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides app.container) { MaterialTheme {
            if (visible.value) DeploymentRevisionContent(editor, template, archive.value, staging.value,
                { error("No picker is used by this test") }, { error("No server archive is staged by this test") },
                { clears++; archive.value = null }, { closes++; visible.value = false; editor.close() }, { accepted++ })
            else Text("Parent page")
        } } }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }

    @Test fun reloadRequiresExplicitDiscardAndFailurePreservesArchiveAndEverySourceField() {
        show("javaJar")
        val staged = ApiResult.Success(DeploymentArchive("synthetic-ref", "review.jar", 123, Long.MAX_VALUE))
        rule.runOnIdle {
            archive.value = staged; editor.baseImage = "base:1"; editor.runtime = "runtime"; editor.entry = "program"
            editor.arguments.addAll(listOf("", " exact value ")); editor.selfContained = true
            editor.result = ApiResult.Transport(null)
            onSnapshot = { ApiResult.Transport(null) }
        }
        dismissFeedback()
        action(R.string.deployments_revision_read_current).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        guardAction(R.string.common_cancel).performClick()
        rule.waitForIdle(); SystemClock.sleep(500)
        rule.runOnIdle { assertEquals(0, reads); assertEquals(0, clears); assertEquals(staged, archive.value) }
        action(R.string.deployments_revision_read_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { !editor.busy && reads == 1 }
        dismissFeedback()
        rule.runOnIdle {
            assertEquals(staged, archive.value); assertEquals("base:1", editor.baseImage); assertEquals("runtime", editor.runtime)
            assertEquals("program", editor.entry); assertEquals(listOf("", " exact value "), editor.arguments.toList())
            assertTrue(editor.selfContained); assertEquals(0, clears); assertEquals(0, sends)
            staging.value = true
        }
        action(R.string.deployments_revision_read_current).assertIsNotEnabled()
        action(R.string.common_close).assertIsNotEnabled()
        rule.runOnIdle { staging.value = false; onSnapshot = { ApiResult.Success(DeploymentSnapshot(it, emptyList(), emptyList(), null)) } }
        action(R.string.deployments_revision_read_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { !editor.busy && clears == 1 }
        rule.runOnIdle { assertNull(archive.value); assertEquals("", editor.entry); assertTrue(editor.arguments.isEmpty()); assertFalse(editor.selfContained); assertEquals(0, sends) }
    }

    @Test fun dispatchedExceptionIsUnknownAndRepeatedReadFailuresNeverReplayOrDiscardImage() {
        show()
        rule.runOnIdle { onSend = { throw IllegalStateException("private send detail") } }
        rule.onNode(hasSetTextAction() and hasText(text(R.string.deployments_image_reference))).performScrollTo().performTextReplacement("nginx:1.27.3-alpine")
        Espresso.closeSoftKeyboard()
        action(R.string.deployments_step_preview).performClick()
        action(R.string.deployments_revision_submit).performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.result != null }
        dismissFeedback()
        action(R.string.deployments_revision_submit).assertIsNotEnabled()
        rule.runOnIdle { assertTrue(editor.unknown); assertEquals(1, sends); assertEquals(0, accepted); onSnapshot = { ApiResult.Transport(null) } }
        repeat(2) {
            action(R.string.deployments_revision_read_current).performScrollTo().performClick()
            guardAction(R.string.editor_discard_changes).performClick()
            rule.waitUntil(5_000) { !editor.busy }
            dismissFeedback()
            rule.runOnIdle { assertEquals("nginx:1.27.3-alpine", editor.image); assertTrue(editor.unknown); assertEquals(1, sends); assertEquals(0, clears) }
        }
        val response = CompletableDeferred<ApiResult<DeploymentSnapshot>>()
        rule.runOnIdle { onSnapshot = { response.await() } }
        action(R.string.deployments_revision_read_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { editor.busy }
        Espresso.pressBack()
        rule.runOnIdle { assertEquals(0, closes); assertEquals("nginx:1.27.3-alpine", editor.image) }
        response.complete(ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)))
        rule.waitUntil(5_000) { !editor.busy && clears == 1 }
        rule.runOnIdle { assertFalse(editor.unknown); assertEquals("", editor.image); assertEquals(1, sends); assertEquals(0, accepted) }
    }
}
