package app.relaxkonos.mobile.ui.manage.deployments

import android.accessibilityservice.AccessibilityServiceInfo
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.SystemClock
import android.view.accessibility.AccessibilityWindowInfo
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsProperties
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
import java.io.File
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DeploymentDefinitionFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val baseline = DeploymentApplication("11111111-1111-1111-1111-111111111111", "review-app", "image", "web", "running", "running", "http", 1, "review-container", 8080, 9080,
        "127.0.0.1", "Site01", "review.invalid", null, null, null, "/ready", DeploymentLimits(1.5, 16777217, 512),
        listOf(DeploymentVolume("data", "/app/data:live", true)),
        listOf(DeploymentDefinitionConfig("TEXT", " value=kept ", false, null), DeploymentDefinitionConfig("TOKEN", "synthetic-secret", true, 7)),
        "2026-10-01T00:00:00.1234567+00:00")
    private var onSnapshot: suspend () -> ApiResult<DeploymentSnapshot> = { ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)) }
    private var onSave: suspend (DeploymentDefinitionUpdate) -> ApiResult<DeploymentApplication> = { ApiResult.Transport(null) }
    private var reads = 0
    private var sends = 0
    private var savedCallbacks = 0
    private var closed = 0
    private val visible = mutableStateOf(true)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: DeploymentDefinitionEditor
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun field(label: Int) = rule.onNode(SemanticsMatcher.keyIsDefined(SemanticsProperties.EditableText) and hasText(text(label)))
    private fun action(label: Int) = rule.onNode(hasClickAction() and hasText(text(label)))
    private fun guardAction(label: Int) = rule.onNode(hasClickAction() and hasText(text(label)) and
        hasAnyAncestor(isDialog() and hasAnyDescendant(hasText(text(R.string.ui_discard_draft_title)))))
    private fun show() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS), privilegedOperations = true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = baseline.id))
            override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> {
                assertEquals(baseline.id, applicationId); reads++; return onSnapshot()
            }
            override suspend fun updateDeploymentDefinition(serverUrl: String, accessToken: String, applicationId: String,
                definition: DeploymentDefinitionUpdate, idempotencyKey: String): ApiResult<DeploymentApplication> {
                assertEquals(baseline.id, applicationId); assertEquals(baseline.updatedAt, definition.expectedUpdatedAt)
                sends++; return onSave(definition)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://definition-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        rule.runOnUiThread {
            rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            editor = DeploymentDefinitionEditor(session, DeploymentRepository(gateway, session), owner, baseline, scope)
        }
        rule.setContent { MaterialTheme {
            if (visible.value) DeploymentDefinitionContent(editor, { closed++; visible.value = false; editor.close() }, { savedCallbacks++ })
            else Text("Parent page")
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }
    private fun screenshot(name: String) {
        rule.waitForIdle(); SystemClock.sleep(500)
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        File(instrumentation.targetContext.getExternalFilesDir(null), name).outputStream().use {
            instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }

    @Test fun invalidAndUnsubmittedFieldsSurviveCloseCancellationThenExplicitDiscard() {
        show()
        field(R.string.deployments_memory_bytes).performScrollTo().assertTextContains("16777217")
        rule.onNodeWithText("data · /app/data:live · ro").assertExists()
        field(R.string.deployments_name).performScrollTo().performTextReplacement("invalid name !")
        field(R.string.deployments_configuration_name).performScrollTo().performTextReplacement("UNSTAGED")
        field(R.string.deployments_configuration_value).performScrollTo().performTextReplacement("unsaved-value")
        field(R.string.deployments_volume_name).performScrollTo().performTextReplacement("unsaved-volume")
        Espresso.closeSoftKeyboard()
        action(R.string.deployments_definition_preview).assertIsNotEnabled()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        guardAction(R.string.common_cancel).performClick()
        field(R.string.deployments_name).assertTextContains("invalid name !")
        field(R.string.deployments_configuration_value).assertTextContains("unsaved-value")
        rule.runOnIdle { assertEquals("UNSTAGED", editor.configName); assertEquals("unsaved-volume", editor.volumeName); assertEquals(0, sends); assertEquals(0, reads); assertEquals(0, closed) }
        action(R.string.common_cancel).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, closed); assertEquals(0, sends); assertEquals(0, savedCallbacks) }
    }

    @Test fun saveExceptionKeepsUnknownDraftAndExplicitReloadFailuresCannotHideOrReplayIt() {
        show()
        rule.runOnIdle { onSave = { throw IllegalStateException("private save detail") } }
        field(R.string.deployments_name).performScrollTo().performTextReplacement("edited-name")
        Espresso.closeSoftKeyboard()
        action(R.string.deployments_definition_preview).performClick()
        action(R.string.deployments_save_definition).performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.result != null }
        rule.runOnIdle { assertTrue(editor.unknown); assertEquals(1, sends); assertEquals(0, savedCallbacks) }
        action(R.string.common_dismiss).performClick()
        action(R.string.deployments_save_definition).assertIsNotEnabled()
        action(R.string.deployments_definition_load_current).performScrollTo().performClick()
        guardAction(R.string.common_cancel).performClick()
        rule.runOnIdle { assertEquals(1, reads); assertEquals("edited-name", editor.draft.name); assertEquals(1, sends) }
        rule.runOnIdle { onSnapshot = { ApiResult.Transport(null) } }
        action(R.string.deployments_definition_load_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { !editor.busy }
        action(R.string.common_dismiss).performClick()
        rule.runOnIdle { assertTrue(editor.unknown); assertEquals("edited-name", editor.draft.name); assertEquals(1, sends) }
        action(R.string.deployments_definition_load_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { !editor.busy }
        action(R.string.common_dismiss).performClick()
        val current = baseline.copy(name = "server-name", updatedAt = "2026-10-01T00:00:02.1234567+00:00")
        val response = CompletableDeferred<ApiResult<DeploymentSnapshot>>()
        rule.runOnIdle { onSnapshot = { response.await() } }
        action(R.string.deployments_definition_load_current).performClick()
        guardAction(R.string.editor_discard_changes).performClick()
        rule.waitUntil(5_000) { editor.busy }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.deployments_edit_definition)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, closed); assertEquals("edited-name", editor.draft.name) }
        response.complete(ApiResult.Success(DeploymentSnapshot(current, emptyList(), emptyList(), null)))
        rule.waitUntil(5_000) { !editor.busy && editor.loadedCurrent }
        field(R.string.deployments_name).performScrollTo().assertTextContains("server-name").assertIsEnabled()
        action(R.string.deployments_definition_preview).assertIsEnabled()
        rule.runOnIdle { assertFalse(editor.unknown); assertFalse(editor.dirty); assertEquals(1, sends); assertEquals(0, savedCallbacks) }
    }

    @Test fun realKeyboardKeepsDefinitionTitleAndClosePreviewActionsVisible() {
        show()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val oldFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            field(R.string.deployments_site).performScrollTo().performClick()
            rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
            screenshot("deployment-definition-keyboard.png")
            val keyboard = Rect().also(automation.windows.single { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }::getBoundsInScreen)
            val origin = IntArray(2)
            Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
                .inRoot(androidx.test.espresso.matcher.RootMatchers.isDialog()).check { view, failure ->
                    if (failure != null) throw failure
                    val inWindow = IntArray(2)
                    view.getLocationOnScreen(origin); view.getLocationInWindow(inWindow)
                    origin[1] -= inWindow[1]
                }
            listOf(R.string.deployments_edit_definition, R.string.common_cancel, R.string.deployments_definition_preview).forEach { label ->
                val bounds = rule.onNodeWithText(text(label)).assertIsDisplayed().fetchSemanticsNode().boundsInWindow
                assertTrue("Definition control $label must be on screen above $keyboard", bounds.top + origin[1] >= 0 && bounds.bottom + origin[1] <= keyboard.top)
            }
            field(R.string.deployments_site).performTextReplacement("EditedSite")
            Espresso.closeSoftKeyboard()
            rule.runOnIdle { assertEquals("EditedSite", editor.draft.siteId); assertEquals(0, sends); assertEquals(0, reads) }
        } finally {
            info.flags = oldFlags
            automation.serviceInfo = info
        }
    }
}
