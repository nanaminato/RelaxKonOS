package app.relaxkonos.mobile.ui.manage.processes

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelStore
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.data.RecentOperationJournal
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.ui.common.*
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ProcessFeedbackFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ManageViewModel
    private lateinit var session: AuthSession
    private val recent = RecentOperationJournal()
    private val process = RemoteProcess(42, "review-process", 0.0, 10, null, 1, "2026-10-01T00:00:00Z")
    private var listedProcesses = listOf(process)
    private var failFirstRead = true
    private var reads = 0
    private var kills = 0
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun show(compact: Boolean = false) {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.PROCESSES)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun queryProcesses(serverUrl: String, accessToken: String, page: Int, pageSize: Int,
                filter: String?, sort: ProcessSort, descending: Boolean): ApiResult<ProcessPage> {
                reads++
                if (reads == 1 && failFirstRead) error("private process provider detail")
                return ApiResult.Success(ProcessPage(listedProcesses, listedProcesses.size, process.startTime!!))
            }
            override suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, expectedStartTime: String): ApiResult<ProcessKillResult> {
                assertEquals(42, pid); kills++; error("private process provider detail")
            }
        }
        session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://process-review.invalid"), "review", "test".toCharArray()) {} }
        rule.runOnUiThread {
            model = ManageViewModel(session, SystemRepository(gateway, session), recent)
            store.put("manage", model)
        }
        rule.setContent { MaterialTheme {
            if (compact) {
                val density = LocalDensity.current
                CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                    Box(Modifier.requiredSize(360.dp, 640.dp)) { ProcessesContent(model, null) }
                }
            } else ProcessesContent(model, null, Modifier.safeDrawingPadding())
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    @Test fun readExceptionCanRetryWithoutExposingPrivateDetailsOrEndingAProcess() {
        show()
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processMessage != null }
        rule.waitUntil(5_000) { rule.onAllNodesWithText(text(R.string.common_retry)).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithText("private process provider detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { reads == 2 && model.processMessage == null && !model.processesLoading }
        rule.runOnIdle { assertEquals(0, kills) }
    }
    @Test fun unknownTerminationFeedbackRetryOnlyReadsAndDoesNotRepeatTheWrite() {
        failFirstRead = false
        show()
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processItems.isNotEmpty() }
        rule.runOnIdle { model.requestKill(process); model.confirmKill() }
        rule.waitUntil(5_000) { model.killMessage != null }
        rule.waitUntil(5_000) { rule.onAllNodesWithText(text(R.string.manage_processes_kill_unknown)).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithText(text(R.string.manage_processes_kill_unknown)).assertIsDisplayed()
        rule.onNodeWithText("private process provider detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { reads == 2 && !model.processesLoading }
        rule.runOnIdle { assertEquals(1, kills); assertNotNull(model.killMessage); assertTrue(recent.entries.value.isEmpty()) }
        rule.onNodeWithText(text(R.string.manage_processes_kill_unknown)).assertIsDisplayed()
    }
    @Test fun sameAccountReloginClearsOldConfirmationAndFactsWithoutSending() {
        failFirstRead = false
        show()
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processItems.isNotEmpty() }
        lateinit var previous: SessionState.Active
        rule.runOnIdle {
            model.requestKill(process); assertNotNull(model.killTarget)
            previous = session.state.value as SessionState.Active
            runBlocking { session.login(ServerConnectionIdentityRules.direct("https://process-review.invalid"), "review", "test".toCharArray()) {} }
        }
        rule.waitUntil(5_000) { model.killTarget == null && model.processItems.isEmpty() }
        rule.runOnIdle {
            model.confirmKill()
            assertNotEquals(previous.sessionInstanceId, (session.state.value as SessionState.Active).sessionInstanceId)
            assertEquals(0, kills); assertTrue(recent.entries.value.isEmpty())
        }
    }
    private fun confirmationSummary() = InstrumentationRegistry.getInstrumentation().targetContext.getString(
        R.string.manage_processes_kill_instance_message, process.name, process.pid, process.startTime)
    @Test fun listOpensDetailsAndCancellingTerminationReturnsWithoutWriting() {
        checkListDetailsAndCancel(compact = false)
    }
    @Test fun narrowLargeFontKeepsDetailsTerminationAndReturnReachable() {
        checkListDetailsAndCancel(compact = true)
    }
    private fun checkListDetailsAndCancel(compact: Boolean) {
        failFirstRead = false
        show(compact)
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processItems.isNotEmpty() }
        rule.onNodeWithText(process.name).performClick()
        val details = rule.onNodeWithTag("process-details")
        details.performScrollToNode(hasClickAction() and hasText(text(R.string.manage_processes_kill)))
        rule.onNode(hasClickAction() and hasText(text(R.string.manage_processes_kill))).performClick()
        rule.onNodeWithText(confirmationSummary()).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.waitUntil(5_000) { model.killTarget == null }
        // Lazy content outside the viewport is not composed; scroll the container before finding its button.
        details.performScrollToNode(hasText(text(R.string.taskmanager_back_to_list)))
        rule.onNodeWithText(text(R.string.taskmanager_back_to_list)).performClick()
        rule.runOnIdle { assertNull(model.processSelected); assertEquals(0, kills) }
        rule.onNodeWithText(process.name).assertIsDisplayed()
    }
    @Test fun refreshedMissingOrReusedProcessWithdrawsVisibleConfirmationWithoutSending() {
        failFirstRead = false
        show()
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processItems.isNotEmpty() }
        for (replacement in listOf(emptyList(), listOf(process.copy(startTime = "2026-10-01T00:00:01Z")))) {
            rule.runOnIdle { listedProcesses = listOf(process); model.loadProcesses() }
            rule.waitUntil(5_000) { !model.processesLoading }
            rule.runOnIdle { model.requestKill(process) }
            rule.onNodeWithText(confirmationSummary()).assertIsDisplayed()
            rule.runOnIdle { listedProcesses = replacement; model.loadProcesses() }
            rule.waitUntil(5_000) { model.killTarget == null && !model.processesLoading }
            rule.onNodeWithText(confirmationSummary()).assertDoesNotExist()
            rule.runOnIdle { model.confirmKill(); assertEquals(0, kills) }
        }
    }
    @Test fun sameInstanceMetricsRefreshRetainsVisibleSummaryAndConfirmSendsOnce() {
        failFirstRead = false
        show()
        rule.runOnIdle { model.startProcessObserving() }
        rule.waitUntil(5_000) { model.processItems.isNotEmpty() }
        rule.runOnIdle { model.requestKill(process) }
        rule.onNodeWithText(confirmationSummary()).assertIsDisplayed()
        rule.runOnIdle { listedProcesses = listOf(process.copy(cpuPercent = 25.0)); model.loadProcesses() }
        rule.waitUntil(5_000) { !model.processesLoading && reads == 2 }
        rule.onNodeWithText(confirmationSummary()).assertIsDisplayed()
        rule.onNode(hasClickAction() and hasText(text(R.string.manage_processes_kill))).performClick()
        rule.waitUntil(5_000) { model.killMessage != null && !model.processesLoading }
        rule.onNodeWithText(confirmationSummary()).assertDoesNotExist()
        rule.runOnIdle { model.confirmKill(); assertEquals(1, kills); assertTrue(recent.entries.value.isEmpty()) }
    }
}
