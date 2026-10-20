package app.relaxkonos.mobile.ui.manage.proxy

import android.graphics.Bitmap
import java.io.File
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.After
import org.junit.Rule
import org.junit.Test

class ProxyTaskFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ProxyViewModel
    private lateinit var owner: SessionState.Active
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Mihomo,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private var current = install
    private val operation = ProxyOperation("55555555-5555-5555-5555-555555555555", "start", ProxyOperationState.Running, "starting", "")
    private val overview = ProxyOverview(ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
        null, true, true, "healthy", "", false, true, false, "linux", false, ProxySystemProxyCapabilities(true, false, true, false), supportsLogs = true)
    private val sent = mutableListOf<String>()
    @Volatile private var operationReads = 0
    @Volatile private var installationReads = 0
    @Volatile private var logReads = 0
    private var logFailure = false
    private var tasks = true
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun start(withTasks: Boolean = true) {
        tasks = withTasks
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.PROXY), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun proxyOverview(serverUrl: String, accessToken: String) = ApiResult.Success(overview)
            override suspend fun proxyProfiles(serverUrl: String, accessToken: String): ApiResult<List<ProxyProfile>> = ApiResult.Success(emptyList())
            override suspend fun proxySubscriptions(serverUrl: String, accessToken: String): ApiResult<List<ProxySubscription>> = ApiResult.Success(emptyList())
            override suspend fun proxyRouting(serverUrl: String, accessToken: String) = ApiResult.Success(ProxyRoutingMode.Rule)
            override suspend fun proxyDownloadOptions(serverUrl: String, accessToken: String) = ApiResult.Success(false)
            override suspend fun proxySettings(serverUrl: String, accessToken: String) = ApiResult.Success(ProxySettings(false, false, true, false, false, "info", 7890, false, "127.0.0.1", null, null))
            override suspend fun proxyRecovery(serverUrl: String, accessToken: String) = ApiResult.Success(ProxyRecovery(false, false, null, ""))
            override suspend fun runtimeResources(serverUrl: String, accessToken: String) = ApiResult.Success(ManagedRuntimeResources(null, emptyList(), "runtime.installation_absent"))
            override suspend fun proxyOperation(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyOperation> {
                operationReads++; return ApiResult.Success(operation)
            }
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(if (tasks) current else null)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                installationReads++; return ApiResult.Success(current)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                sent += operationId; return ApiResult.Transport(null)
            }
            override suspend fun proxyLogs(serverUrl: String, accessToken: String): ApiResult<List<ProxyLog>> {
                logReads++; return if (logFailure) ApiResult.Transport(null) else ApiResult.Success(listOf(ProxyLog(1, "info", "review-log")))
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://proxy-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = ProxyViewModel(session, ProxyRepository(gateway, session, index, ProxyRequestJournal(storage())),
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(storage())),
                index, ElevationAnswerProvider.Declines)
            store.put("proxy", model)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                ProxyContent(model, owner, {}, modifier = Modifier.widthIn(max = 360.dp).fillMaxHeight().statusBarsPadding())
            }
        } }
        rule.waitUntil(5_000) { !model.state.busy && model.state.overview is ApiResult.Success }
    }
    private fun tab(id: Int) = rule.onAllNodesWithText(text(id)).filter(hasClickAction()).onFirst()
        .performScrollTo().performClick().assertIsSelected()
    @Test fun cancellationIsWithdrawnOnPauseAndReplacementRequiresNewConfirmation() {
        start(); tab(R.string.workspace_records)
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        val old = requireNotNull(model.installationCancellation)
        val before = installationReads
        Thread.sleep(2_000)
        rule.runOnIdle { assertEquals(before, installationReads); assertSame(old, model.installationCancellation) }
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertNull(model.installationCancellation); model.cancelInstall(old); assertTrue(sent.isEmpty()) }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.mainClock.autoAdvance = true
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        val replaced = requireNotNull(model.installationCancellation)
        rule.runOnUiThread { current = install.copy(operationId = "66666666-6666-6666-6666-666666666666"); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancelInstall(replaced); assertTrue(sent.isEmpty()) }
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        rule.onAllNodesWithText(current.operationId).onLast().assertIsDisplayed()
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val screenshot = instrumentation.uiAutomation.takeScreenshot()
        try {
            File(instrumentation.targetContext.getExternalFilesDir(null), "proxy-cancellation-review.png").outputStream().use {
                screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally { screenshot.recycle() }
        rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.busy && sent.size == 1 }
        rule.runOnIdle { assertEquals(listOf(current.operationId), sent); assertFalse(model.state.installationVerified) }
    }
    @Test fun backgroundStopsBothTaskQueriesAndForegroundResumesBoth() {
        start()
        rule.runOnUiThread { model.recoverOperation(operation.operationId) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        val originalOperations = operationReads; val originalInstalls = installationReads
        rule.waitUntil(8_000) { operationReads > originalOperations && installationReads > originalInstalls && !model.state.busy }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle(); rule.waitUntil(5_000) { !model.state.busy }
        val pausedOperations = operationReads; val pausedInstalls = installationReads
        Thread.sleep(2_000)
        assertEquals(pausedOperations, operationReads); assertEquals(pausedInstalls, installationReads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(8_000) { operationReads > pausedOperations && installationReads > pausedInstalls && !model.state.busy }
    }
    @Test fun logsStopInBackgroundAndFailureRecoversOnNextForegroundSample() {
        start(false); tab(R.string.workspace_logs)
        rule.waitUntil(5_000) { logReads > 0 && !model.state.diagnosticsBusy }
        rule.onNodeWithText("review-log", substring = true).assertExists()
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        val paused = logReads
        Thread.sleep(3_300)
        assertEquals(paused, logReads)
        rule.runOnUiThread { logFailure = true }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(5_000) { logReads > paused && model.state.diagnosticsFailed && !model.state.diagnosticsBusy }
        rule.runOnUiThread { logFailure = false }
        val failed = logReads
        rule.waitUntil(8_000) { logReads > failed && !model.state.diagnosticsFailed && !model.state.diagnosticsBusy }
        rule.onNodeWithText("review-log", substring = true).assertExists()
        tab(R.string.workspace_overview)
        val left = logReads
        Thread.sleep(3_300)
        assertEquals(left, logReads)
        rule.runOnIdle { assertTrue(sent.isEmpty()) }
    }
}
