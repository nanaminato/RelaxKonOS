package app.relaxkonos.mobile.ui.manage.operations

import android.net.Uri
import android.graphics.Bitmap
import java.io.File
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import kotlinx.coroutines.*
import org.junit.*
import org.junit.Assert.*

class OperationsRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: OperationsViewModel
    private lateinit var owner: SessionState.Active
    private lateinit var session: AuthSession
    private var cancelRead = false
    private var writes = 0
    private var failDiagnostics = false
    private var diagnosticReads = 0
    private var exports = 0
    private var exportResult = false
    private val exportedReports = mutableListOf<String>()
    private val refreshReads = java.util.concurrent.atomic.AtomicInteger()
    private val hiddenIds = java.util.concurrent.CopyOnWriteArrayList<String>()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun show(includeSecond: Boolean = false) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
        }
        session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://operations-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        val item = ObservedOperation(OperationReference(owner.serviceId, owner.userName, OperationDomain.Deployment,
            "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111", 1),
            OperationCheck.Verified, "review-operation", state = "running", cancellable = true)
        val actions = object : OperationsActions {
            override suspend fun refresh(owner: SessionState.Active): OperationCenterSnapshot {
                refreshReads.incrementAndGet()
                if (cancelRead) throw CancellationException("private read detail")
                return OperationCenterSnapshot(listOfNotNull(item, if (includeSecond) item.copy(
                    reference = item.reference.copy(operationId = "33333333-3333-3333-3333-333333333333"), target = "other-operation") else null)
                    .filterNot { it.reference.operationId in hiddenIds }, false)
            }
            override suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation): ApiResult<List<String>> {
                diagnosticReads++
                if (failDiagnostics) throw IllegalStateException("private diagnostic detail")
                return ApiResult.Success(listOf("review-diagnostic"))
            }
            override suspend fun cancel(owner: SessionState.Active, item: ObservedOperation): ApiResult<*> {
                writes++; throw CancellationException("private cancellation detail")
            }
            override suspend fun hide(owner: SessionState.Active, item: ObservedOperation) { hiddenIds += item.reference.operationId }
            override suspend fun recoverInstallation(owner: SessionState.Active, id: String): ApiResult<*> = throw CancellationException("private lookup detail")
            override suspend fun exportReport(uri: Uri, report: String): Boolean {
                exports++; exportedReports += report; return exportResult
            }
        }
        rule.runOnUiThread { model = OperationsViewModel(session, actions); store.put("operations", model) }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalAppContainer provides app.container, LocalDensity provides Density(density.density, 1.6f)) {
                val active by session.state.collectAsState()
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) {
                    (active as? SessionState.Active)?.let { OperationsContent(model, it) }
                }
            }
        } }
        rule.waitUntil(5_000) { model.state.owner === owner && !model.state.loading && model.state.items.isNotEmpty() }
        rule.waitUntil(5_000) {
            var focused = false
            rule.runOnUiThread { focused = rule.activity.window.decorView.hasWindowFocus() }
            focused
        }
    }
    private fun acknowledge() { rule.onNodeWithText(text(R.string.common_dismiss)).performClick() }
    private fun relogin() {
        rule.runOnIdle { runBlocking {
            session.login(ServerConnectionIdentityRules.direct("https://operations-review.invalid"), "review", "test".toCharArray()) {}
        } }
        rule.waitUntil(5_000) { model.state.owner === session.state.value && !model.state.loading && model.state.items.isNotEmpty() }
    }
    @Test fun reloginClearsOldReportAndApprovalAndRejectsOldExport() {
        show()
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText("review-diagnostic").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.operations_export_diagnostics)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val previous = owner
        relogin()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { runBlocking {
            assertFalse(model.exportReport(previous, Uri.parse("content://operations-review/export"), "old diagnostic report"))
            assertEquals(0, exports)
        } }
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_hide)).performScrollTo().performClick()
        val oldOwner = rule.runOnIdle { requireNotNull(model.state.owner) }
        val review = rule.runOnIdle { requireNotNull(model.state.hideReview) }
        relogin()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { model.hide(oldOwner, review); assertTrue(hiddenIds.isEmpty()); assertEquals(0, writes) }
        rule.runOnIdle { runBlocking {
            exportResult = true
            assertTrue(model.exportReport(requireNotNull(model.state.owner), Uri.parse("content://operations-review/export"), "current diagnostic report"))
            assertEquals(listOf("current diagnostic report"), exportedReports)
        } }
    }
    private fun pauseAndResumeWithoutFrames() {
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
    }
    @Test fun pausedApprovalsAreWithdrawnAndOldCallbacksCannotExecute() {
        show()
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        val cancel = rule.runOnIdle { requireNotNull(model.state.cancelReview) }
        pauseAndResumeWithoutFrames()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { model.cancel(owner, cancel); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.operations_hide)).performScrollTo().performClick()
        val hide = rule.runOnIdle { requireNotNull(model.state.hideReview) }
        pauseAndResumeWithoutFrames()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { model.hide(owner, hide); assertTrue(hiddenIds.isEmpty()); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(hasText("review-operation") and hasAnyAncestor(isDialog())).assertIsDisplayed()
        rule.onNode(hasText(text(R.string.common_close)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
    }
    @Test fun changingSelectionWithdrawsHideAndFreshReviewNamesTheTarget() {
        show(includeSecond = true)
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_hide)).performScrollTo().performClick()
        rule.onNode(hasText("review-operation") and hasAnyAncestor(isDialog())).assertIsDisplayed()
        rule.runOnIdle { model.select(owner, model.state.items.last()) }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.operations_hide)).performScrollTo().performClick()
        rule.onNode(hasText("other-operation") and hasAnyAncestor(isDialog())).assertIsDisplayed()
        rule.onNode(hasText("33333333-3333-3333-3333-333333333333") and hasAnyAncestor(isDialog())).assertIsDisplayed()
        capture("operations-hide-review.png")
        rule.onNode(hasText(text(R.string.operations_hide)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { !model.state.hiding && model.state.items.size == 1 }
        rule.runOnIdle {
            assertEquals(listOf("33333333-3333-3333-3333-333333333333"), hiddenIds)
            assertEquals("review-operation", model.state.items.single().target); assertEquals(0, writes)
        }
    }
    private fun capture(name: String) {
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        automation.waitForIdle(500, 5_000)
        assertEquals(context.packageName, automation.rootInActiveWindow?.packageName?.toString())
        val bitmap = automation.takeScreenshot()
        try {
            File(context.getExternalFilesDir(null), name).outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        } finally { bitmap.recycle() }
    }
    @Test fun failedDiagnosticsHasReadOnlyRetryAndExportPreviewUsesRecoveredData() {
        show()
        rule.runOnIdle { failDiagnostics = true }
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_diagnostics_failed)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("private diagnostic", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).performScrollTo().assertIsEnabled()
        capture("operations-diagnostics-failed.png")
        rule.runOnIdle { failDiagnostics = false }
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { !model.state.diagnosticsLoading && model.state.diagnostics is ApiResult.Success }
        rule.onNodeWithText("review-diagnostic").performScrollTo().assertIsDisplayed()
        val beforePoll = refreshReads.get()
        rule.waitUntil(8_000) { refreshReads.get() > beforePoll }
        rule.onNodeWithText("review-diagnostic").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.operations_diagnostics_failed)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.operations_export_diagnostics)).performScrollTo().performClick()
        rule.onNode(hasText("review-diagnostic", substring = true) and hasAnyAncestor(isDialog())).performScrollTo().assertIsDisplayed()
        rule.onNode(hasText("\"diagnosticsAvailable\": true", substring = true) and hasAnyAncestor(isDialog())).assertExists()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.runOnIdle { assertEquals(2, diagnosticReads); assertEquals(0, writes); assertEquals(0, exports) }
    }
    @Test fun backgroundStopsPollingAndResumeReadsOriginalTasks() {
        show()
        val activity = rule.activity
        val before = refreshReads.get()
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
            assertEquals(Lifecycle.State.CREATED, rule.activityRule.scenario.state)
            android.os.SystemClock.sleep(5_600)
            assertEquals(before, refreshReads.get())
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.waitUntil(8_000) { refreshReads.get() > before }
        rule.runOnIdle {
            assertSame(activity, rule.activity)
            assertFalse(model.state.error); assertEquals(0, writes)
            assertEquals("11111111-1111-1111-1111-111111111111", model.state.items.single().reference.operationId)
        }
    }
    @Test fun cancelledReadEnablesExplicitRefreshAndWithdrawsVerifiedFacts() {
        show()
        rule.runOnIdle { cancelRead = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.error && !model.state.loading }
        rule.runOnIdle { assertEquals(OperationCheck.Unavailable, model.state.items.single().check); assertEquals(0, writes) }
        acknowledge()
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { cancelRead = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.loading && !model.state.error }
        rule.runOnIdle { assertEquals(OperationCheck.Verified, model.state.items.single().check); assertEquals(0, writes) }
    }
    @Test fun cancelledCancelRemovesOldActionAndReadOnlyRefreshNeverResends() {
        show()
        rule.onNodeWithText("review-operation").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { model.state.error && !model.state.cancelling }
        rule.runOnIdle { assertEquals(1, writes); assertEquals(OperationCheck.Unavailable, model.state.items.single().check) }
        acknowledge()
        rule.onNodeWithText(text(R.string.operations_request_cancel)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.loading && !model.state.error }
        rule.runOnIdle { assertEquals(1, writes) }
    }
    @Test fun cancelledInstallationLookupRestoresPageRefreshWithoutWriting() {
        show()
        rule.onNodeWithText(text(R.string.installation_recover)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.installation_operation_id)).performTextInput("11111111-1111-1111-1111-111111111111")
        rule.onNode(hasText(text(R.string.common_refresh)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { model.state.error && !model.state.loading }
        acknowledge()
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
}
