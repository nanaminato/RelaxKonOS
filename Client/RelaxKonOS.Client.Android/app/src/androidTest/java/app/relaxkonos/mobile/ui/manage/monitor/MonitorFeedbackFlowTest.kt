package app.relaxkonos.mobile.ui.manage.monitor

import android.graphics.Bitmap
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import java.io.File
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class MonitorFeedbackFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: MonitorViewModel
    private lateinit var session: AuthSession
    private var failed = true
    private var reads = 0
    private var connections = 0
    private val info = PerformanceInfo(CpuInfo("review-cpu", 1, 2, null, null, null, null, null, null),
        MemoryInfo(1024, null), emptyList(), emptyList(), emptyList(),
        PerformanceCapabilities(false, false, false, false, false, false, false, false))
    private val sample = PerformanceSnapshot(1, "2026-10-01T00:00:00Z",
        CpuRealtimeMetrics(25.0, null, null, null, null, emptyList(), null, null, null, null),
        MemoryRealtimeMetrics(1024, 256, 768, null, null, null, null),
        emptyList(), emptyList(), emptyList(), 100, PerformanceHealth(false, null, null))
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private fun login() = runBlocking {
        session.login(ServerConnectionIdentityRules.direct("https://monitor-review.invalid"), "review", "test".toCharArray()) {}
    }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.METRICS)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun performanceInfo(serverUrl: String, accessToken: String) = ApiResult.Success(info)
            override suspend fun networkAddresses(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<NetworkAddress>())
            override suspend fun performanceHistory(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<PerformanceSnapshot>())
            override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> {
                reads++
                if (failed) error("private metrics provider detail")
                return ApiResult.Success(sample)
            }
        }
        session = AuthSession(gateway); login()
        rule.runOnUiThread {
            model = MonitorViewModel(session, SystemRepository(gateway, session), PerformanceConnectionFactory { _, _, _, _ ->
                connections++
                object : PerformanceConnection {
                    override suspend fun connect() { error("isolated offline transport") }
                    override suspend fun subscribe() = Unit
                    override suspend fun disconnect() = Unit
                }
            })
            store.put("monitor", model)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) { MonitorContent(model, null) }
            }
        } }
        rule.runOnIdle { model.observe() }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun screenshot(name: String) {
        rule.waitForIdle()
        File(context.getExternalFilesDir(null), name).outputStream().use {
            InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }
    @Test fun failedSnapshotCanRetryAndOpenDetailsThenReturnAtLargeFont() {
        show()
        rule.waitUntil(5_000) { model.state.value.phase == PerformancePhase.Failed }
        rule.onNodeWithText("private metrics provider detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).assertIsDisplayed()
        screenshot("monitor-read-failure.png")
        rule.runOnIdle { failed = false }
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { model.state.value.phase == PerformancePhase.Snapshot && model.state.value.problem == null }
        rule.onNodeWithText(text(R.string.home_label_cpu)).performClick()
        rule.runOnIdle { assertEquals(PerformanceKind.Cpu, model.selected?.kind); assertTrue(reads >= 2) }
        rule.onNode(hasScrollAction()).performScrollToNode(hasText(text(R.string.monitor_details)))
        rule.onNodeWithText(text(R.string.monitor_details)).assertIsDisplayed()
        rule.onNodeWithText("review-cpu").performScrollTo().assertIsDisplayed()
        screenshot("monitor-cpu-details-large-font.png")
        rule.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        rule.runOnIdle { assertNull(model.selected) }
        rule.onNodeWithText(text(R.string.home_label_memory)).assertIsDisplayed()
    }
    @Test fun newLoginClearsSelectionAndLeavingClearsFactsAndRejectsRetry() {
        failed = false; show()
        rule.waitUntil(5_000) { model.state.value.phase == PerformancePhase.Snapshot }
        rule.onNodeWithText(text(R.string.home_label_cpu)).performClick()
        rule.runOnIdle { login() }
        rule.waitUntil(5_000) { model.selected == null && model.state.value.phase == PerformancePhase.Snapshot }
        rule.runOnIdle {
            model.stopObserving()
            val beforeReads = reads; val beforeConnections = connections
            model.retry()
            assertEquals(beforeReads, reads); assertEquals(beforeConnections, connections)
            assertEquals(PerformanceState(), model.state.value)
        }
    }
}
