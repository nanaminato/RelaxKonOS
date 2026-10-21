package app.relaxkonos.mobile.ui.manage.operations

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import org.junit.*
import org.junit.Assert.*

class AlertRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var browser: EventAlertBrowser
    private var failPage = false
    private var failDetail = false
    private var writes = 0
    private var current = OperationalAlert("review-alert", "deployment.operation_failed", "error", "open", "deployment.failed",
        1, 1, "applicationDeployment", "review-application", null, 0, "review-event", null, null, null)
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { if (::browser.isInitialized) browser.stop(); scope.cancel() } }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.EVENT_ALERTS), true),
                    AuthTokens("review-access", "review-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun eventAlertSummary(serverUrl: String, accessToken: String) =
                ApiResult.Success(EventAlertSummary(1, 0, 0, "error", 1))
            override suspend fun alerts(serverUrl: String, accessToken: String, cursor: String?, query: AlertQuery): ApiResult<OperationalAlertPage> {
                if (failPage) throw IllegalStateException("private page detail")
                return ApiResult.Success(OperationalAlertPage(listOf(current), null))
            }
            override suspend fun alertDetail(serverUrl: String, accessToken: String, id: String): ApiResult<OperationalAlertDetail> {
                if (failDetail) throw IllegalStateException("private alert detail")
                return ApiResult.Success(OperationalAlertDetail(current, emptyList(), emptyList()))
            }
            override suspend fun mutateAlert(serverUrl: String, accessToken: String, id: String, action: AlertMutation,
                reason: String?, expiresAt: String?): ApiResult<OperationalAlert> {
                writes++; throw IllegalStateException("private send detail")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://alert-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        browser = EventAlertBrowser(EventAlertRepository(gateway, session), scope)
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Column(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp).verticalScroll(rememberScrollState())) {
                    AlertPanelContent(owner, browser) {}
                }
            }
        } }
        rule.waitUntil(5_000) { browser.state.value.active && !browser.state.value.loading }
        rule.waitUntil(5_000) {
            InstrumentationRegistry.getInstrumentation().uiAutomation.rootInActiveWindow?.packageName?.toString() == context.packageName
        }
    }
    private fun acknowledge() {
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
    }
    @Test fun pauseWithoutComposeFrameWithdrawsConfirmationAndRejectsOldSubmit() {
        show()
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_acknowledge)).performScrollTo().performClick()
        val submit = rule.onNode(hasText(text(R.string.operations_acknowledge)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.waitUntil(5_000) { browser.state.value.active && !browser.state.value.loading && !browser.state.value.detailLoading }
        rule.runOnIdle { submit() }
        rule.waitForIdle()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun unexpectedReadsShowSafeFailureAndCanRecoverFromProductionActions() {
        failPage = true; show()
        rule.onNodeWithText(text(R.string.operations_alerts_unavailable)).assertIsDisplayed()
        rule.onNodeWithText("private page detail").assertDoesNotExist()
        acknowledge()
        rule.runOnIdle { failPage = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { failDetail = true }
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performClick()
        rule.onNodeWithText(text(R.string.operations_alerts_unavailable)).assertIsDisplayed()
        acknowledge()
        rule.runOnIdle { failDetail = false }
        // Selecting the original row retries the same detail without a write.
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performScrollTo().performClick()
        rule.onNodeWithText("review-alert").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertFalse(browser.state.value.detailLoading); assertEquals(0, writes) }
    }
    @Test fun failedReconciliationKeepsOriginalTargetAndVisibleRetry() {
        show()
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_acknowledge)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.operations_acknowledge)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { browser.state.value.unknown && !browser.state.value.actionBusy }
        rule.onNodeWithText(text(R.string.event_center_unknown)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { failDetail = true }
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_alerts_unavailable)).assertIsDisplayed()
        acknowledge()
        rule.onNodeWithText("review-alert").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.event_center_unknown)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().assertIsDisplayed()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        automation.waitForIdle(500, 5_000)
        assertEquals(context.packageName, automation.rootInActiveWindow?.packageName?.toString())
        val bitmap = automation.takeScreenshot()
        try {
            java.io.File(context.getExternalFilesDir(null), "alert-reconcile-failed.png").outputStream().use {
                bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally { bitmap.recycle() }
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_alerts_unavailable)).assertIsDisplayed()
        val dismiss = rule.onNodeWithText(text(R.string.common_dismiss)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.runOnIdle { dismiss(); browser.reconcile() }
        rule.onNodeWithText(text(R.string.operations_alerts_unavailable)).assertIsDisplayed()
        acknowledge()
        rule.runOnIdle { failDetail = false; current = current.copy(status = "acknowledged") }
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !browser.state.value.detailLoading && !browser.state.value.unknown }
        rule.onNodeWithText("review-alert").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertTrue(browser.state.value.detail is ApiResult.Success); assertEquals(1, writes) }
    }
    @Test fun unexpectedSendShowsUnknownAndReadOnlyReconciliationNeverReplays() {
        show()
        rule.onNodeWithText(text(R.string.alert_deployment_failed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.operations_acknowledge)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.operations_acknowledge)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { browser.state.value.unknown && !browser.state.value.actionBusy }
        rule.onNodeWithText(text(R.string.event_center_unknown)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.operations_acknowledge)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { current = current.copy(status = "acknowledged") }
        rule.onNodeWithText(text(R.string.event_center_reconcile)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !browser.state.value.unknown && !browser.state.value.detailLoading }
        rule.runOnIdle { assertEquals(1, writes); assertTrue(browser.state.value.detail is ApiResult.Success) }
        rule.onNodeWithText("private send detail").assertDoesNotExist()
    }
}
