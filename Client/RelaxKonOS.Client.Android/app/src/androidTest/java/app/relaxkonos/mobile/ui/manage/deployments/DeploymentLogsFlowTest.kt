package app.relaxkonos.mobile.ui.manage.deployments

import android.graphics.Bitmap
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import java.io.File
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DeploymentLogsFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var browser: DeploymentBrowser
    private val tails = mutableListOf<Int>()
    private var onLogs: suspend (Int) -> ApiResult<DeploymentLog> = { ApiResult.Success(DeploymentLog(listOf("review old log"), true)) }
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun action(id: Int) = rule.onNode(hasClickAction() and hasText(text(id)))
    private fun tailAction(id: Int, tail: Int) = rule.onNodeWithText(InstrumentationRegistry.getInstrumentation().targetContext.getString(id, tail))
    private fun show() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun deploymentTemplates(serverUrl: String, accessToken: String): ApiResult<List<DeploymentTemplate>> = ApiResult.Success(emptyList())
            override suspend fun applicationCatalog(serverUrl: String, accessToken: String): ApiResult<List<CatalogTemplate>> = ApiResult.Success(emptyList())
            override suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>> = ApiResult.Success(emptyList())
            override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> = ApiResult.Transport(null)
            override suspend fun deploymentLogs(serverUrl: String, accessToken: String, applicationId: String, tail: Int): ApiResult<DeploymentLog> {
                assertEquals("review-application", applicationId); tails.add(tail); return onLogs(tail)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://logs-review.invalid"), "review", "test".toCharArray()) {} }
        rule.runOnUiThread { browser = DeploymentBrowser(DeploymentRepository(gateway, session), session, scope); browser.select("review-application") }
        rule.setContent { MaterialTheme {
            val state by browser.state.collectAsState()
            Column(Modifier.safeDrawingPadding().verticalScroll(rememberScrollState())) {
                DeploymentLogsContent(state, { browser.loadLogs() }, browser::loadMoreLogs, browser::retryLogs)
            }
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }

    @Test fun expandedReadFailureRetriesSameTailAndReplacesOldOutput() {
        show()
        rule.runOnIdle { assertTrue(tails.isEmpty()) }
        tailAction(R.string.deployments_logs_load_initial, 20).performClick()
        rule.waitUntil(5_000) { browser.state.value.logs is ApiResult.Success }
        rule.onNodeWithText("review old log").assertIsDisplayed()
        rule.runOnIdle { onLogs = { throw IllegalStateException("private log detail") } }
        tailAction(R.string.deployments_logs_load_more, 100).performScrollTo().performClick()
        rule.waitUntil(5_000) { browser.state.value.logs is ApiResult.Transport && !browser.state.value.logsLoading }
        rule.onNodeWithText(text(R.string.deployments_logs_read_failed)).assertIsDisplayed()
        rule.onNodeWithText("review old log").assertDoesNotExist()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val evidence = File(instrumentation.targetContext.getExternalFilesDir(null), "deployment-logs-retry.png")
        evidence.outputStream().use { instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it) }
        val receipt = CompletableDeferred<ApiResult<DeploymentLog>>()
        rule.runOnIdle { onLogs = { receipt.await() } }
        action(R.string.deployments_logs_retry).performClick()
        rule.waitUntil(5_000) { browser.state.value.logsLoading }
        action(R.string.deployments_logs_retry).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.deployments_logs_read_failed)).assertIsDisplayed()
        receipt.complete(ApiResult.Success(DeploymentLog(listOf("review new log"), false)))
        rule.waitUntil(5_000) { !browser.state.value.logsLoading }
        rule.onNodeWithText("review new log").assertIsDisplayed()
        rule.onNodeWithText("review old log").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.deployments_logs_read_failed)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(listOf(20, 100, 100), tails) }
    }

    @Test fun repeatedPermissionFailuresRemainInlineAndCanRecoverWithoutDismissal() {
        onLogs = { ApiResult.Problem(403, "review-denied", null) }
        show()
        tailAction(R.string.deployments_logs_load_initial, 20).performClick()
        repeat(2) { attempt ->
            rule.waitUntil(5_000) { !browser.state.value.logsLoading && tails.size == attempt + 1 }
            rule.onNodeWithText(text(R.string.deployments_permission)).assertIsDisplayed()
            rule.onNodeWithText(text(R.string.common_dismiss)).assertDoesNotExist()
            if (attempt == 0) action(R.string.deployments_logs_retry).performClick()
        }
        rule.runOnIdle { onLogs = { ApiResult.Success(DeploymentLog(emptyList(), false)) } }
        action(R.string.deployments_logs_retry).performClick()
        rule.waitUntil(5_000) { browser.state.value.logs is ApiResult.Success }
        rule.onNodeWithText(text(R.string.deployments_logs_empty)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.deployments_permission)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(listOf(20, 20, 20), tails) }
    }
}
