package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.SemanticsActions
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
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import android.graphics.Bitmap
import java.io.File
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver

class OutboundProxyFeedbackFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: OutboundProxyEditor
    private var reads = 0
    private var writes = 0
    private var cancelRead = false
    private var pendingRead: CompletableDeferred<ApiResult<OutboundProxyStatus>>? = null
    private val active = mutableStateOf(true)
    private var shellBacks = 0
    private var pageBacks = 0
    @Volatile private var foreground = true
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private val status = OutboundProxyStatus(
        OutboundProxySettings(false, OutboundProxySource.Custom, "", "", "localhost", false, false, false, false),
        emptyList(), "", "", "", "", false, "linux", null)

    private fun show(initiallyActive: Boolean = true) {
        active.value = initiallyActive
        rule.activityRule.scenario.onActivity { activity ->
            activity.lifecycle.addObserver(LifecycleEventObserver { owner, _ ->
                foreground = owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
            })
        }
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun outboundProxyStatus(serverUrl: String, accessToken: String): ApiResult<OutboundProxyStatus> {
                reads++
                pendingRead?.let { return it.await() }
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(status)
            }
            override suspend fun clearOutboundProxy(serverUrl: String, accessToken: String): ApiResult<OutboundProxyStatus> {
                writes++
                throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://proxy-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val repository = DockerRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = Unit
        }), app.container.dockerMutationGate)
        rule.runOnUiThread { editor = OutboundProxyEditor(repository, owner, { session.state.value === owner }, scope) }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) {
                    BackHandler { shellBacks++ }
                    WorkspaceSection(active.value) {
                        // Match MoreWorkspace: a retained hidden category receives no back callback.
                        OutboundProxyContent(owner, editor, ({ pageBacks++; Unit }).takeIf { active.value }, null, active.value)
                    }
                }
            }
        } }
        rule.waitUntil(5_000) {
            var focused = false
            rule.runOnUiThread { focused = rule.activity.window.decorView.hasWindowFocus() }
            focused
        }
        if (initiallyActive) rule.waitUntil(5_000) { editor.canSubmit }
    }
    @After fun cleanup() {
        rule.runOnUiThread { if (::editor.isInitialized) editor.close(); scope.cancel() }
    }
    private fun refreshAndVerify() {
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.proxy_discard)).performClick()
        rule.waitUntil(5_000) { editor.canSubmit }
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().assertIsEnabled()
    }
    private fun acknowledgeFeedback() {
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.waitUntil(5_000) { rule.onAllNodesWithText(text(R.string.common_dismiss)).fetchSemanticsNodes().isEmpty() }
    }
    @Test fun retainedHiddenProxyDoesNotReadUntilSelected() {
        show(false)
        rule.runOnIdle { assertEquals(0, reads); assertNull(editor.draft); active.value = true }
        rule.waitUntil(5_000) { editor.canSubmit }
        rule.runOnIdle { assertEquals(1, reads); assertEquals(0, writes) }
    }
    @Test fun hiddenReadFailureWaitsForProxyPageBeforeShowingFeedback() {
        show()
        val response = CompletableDeferred<ApiResult<OutboundProxyStatus>>()
        rule.runOnIdle { pendingRead = response; editor.refresh() }
        rule.waitUntil(5_000) { reads == 2 && editor.busy }
        rule.runOnIdle { active.value = false; response.complete(ApiResult.Transport(null)) }
        rule.waitUntil(5_000) { !editor.busy && editor.message != null }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertNull(editor.status); assertEquals(0, writes); active.value = true }
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        rule.runOnIdle { active.value = false }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { active.value = true }
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        acknowledgeFeedback()
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { pendingRead = null }
        refreshAndVerify()
        rule.runOnIdle { assertEquals(3, reads); assertEquals(0, writes) }
    }
    @Test fun hiddenProxyWithdrawsApprovalButPreservesDraft() {
        show()
        rule.onNodeWithText(text(R.string.proxy_show_urls)).performScrollTo().performClick().assertIsOn()
        rule.runOnIdle { editor.change { it.copy(noProxy = "unsaved.invalid") }; editor.save() }
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { active.value = false }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertNull(editor.review); active.value = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.proxy_show_urls)).performScrollTo().assertIsOff()
        rule.runOnIdle {
            assertEquals("unsaved.invalid", editor.draft?.noProxy)
            assertEquals(1, reads); assertEquals(0, writes)
            editor.save()
        }
        rule.onNode(isDialog()).assertExists()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
    }
    @Test fun pausedProxyWithdrawsApprovalWithoutRecomposition() {
        show()
        rule.onNodeWithText(text(R.string.proxy_show_urls)).performScrollTo().performClick().assertIsOn()
        rule.runOnIdle { editor.change { it.copy(noProxy = "unsaved.invalid") }; editor.clear() }
        rule.onNode(isDialog()).assertExists()
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.waitUntil(5_000) { !foreground }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { foreground }
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.proxy_show_urls)).performScrollTo().assertIsOff()
        rule.runOnIdle {
            assertNull(editor.review); assertEquals("unsaved.invalid", editor.draft?.noProxy)
            assertEquals(1, reads); assertEquals(0, writes)
        }
    }
    @Test fun hiddenProxyDraftDoesNotConsumeAnotherCategoriesBack() {
        show()
        rule.runOnIdle {
            editor.change { it.copy(noProxy = "unsaved.invalid") }
            assertTrue(editor.dirty)
            active.value = false
        }
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.runOnIdle {
            assertEquals(1, shellBacks)
            assertEquals(0, pageBacks)
            assertNull(editor.review)
            assertEquals("unsaved.invalid", editor.draft?.noProxy)
            active.value = true
        }
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.onNodeWithText(text(R.string.proxy_discard_title)).assertIsDisplayed()
        rule.runOnIdle {
            assertEquals(ProxyReview.Leave, editor.review)
            assertEquals(1, shellBacks)
            assertEquals(0, pageBacks)
            assertEquals(0, writes)
        }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertNull(editor.review); assertTrue(editor.dirty) }
    }
    @Test fun cancelledWriteShowsUnknownAndRequiresReadBeforeAnotherSubmission() {
        show()
        rule.onNodeWithText(text(R.string.proxy_clear)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.proxy_apply)).performClick()
        rule.waitUntil(5_000) { !editor.busy && editor.message != null }
        rule.onNodeWithText(text(R.string.proxy_result_unknown)).performScrollTo().assertIsDisplayed()
        rule.waitForIdle()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        assertEquals(context.packageName, InstrumentationRegistry.getInstrumentation()
            .uiAutomation.rootInActiveWindow?.packageName?.toString())
        File(context.getExternalFilesDir(null), "proxy-unknown-large-font.png").outputStream().use { output ->
            val bitmap = InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
            try { bitmap.compress(Bitmap.CompressFormat.PNG, 100, output) } finally { bitmap.recycle() }
        }
        rule.onNodeWithText("private cancelled", substring = true).assertDoesNotExist()
        acknowledgeFeedback()
        rule.onNodeWithText(text(R.string.proxy_clear)).performScrollTo().assertIsNotEnabled().performClick()
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertNull(editor.review) }
        refreshAndVerify()
        rule.runOnIdle { assertEquals(2, reads); assertEquals(1, writes) }
    }
    @Test fun cancelledReadDisablesChangesUntilExplicitRefresh() {
        show()
        rule.runOnIdle { cancelRead = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { reads == 2 && !editor.busy }
        rule.onNodeWithText(text(R.string.error_connectivity)).performScrollTo().assertIsDisplayed()
        acknowledgeFeedback()
        rule.onNodeWithText(text(R.string.proxy_clear)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { cancelRead = false }
        refreshAndVerify()
        rule.runOnIdle { assertEquals(3, reads); assertEquals(0, writes) }
    }
    @Test fun repeatedImmediateFailureReopensAcknowledgedFeedback() {
        show()
        rule.runOnIdle { cancelRead = true; editor.load() }
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        // Acknowledge and finish the next immediate request before another frame can
        // remove the old feedback composition. Identical wording is still a new event.
        val dismiss = rule.onNodeWithText(text(R.string.common_dismiss))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.runOnIdle {
            dismiss()
            editor.load()
        }
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(3, reads); assertEquals(0, writes); assertFalse(editor.canSubmit) }
    }
}
