package app.relaxkonos.mobile.ui.home

import android.graphics.Bitmap
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.ui.common.mobileApplications
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.RelaxKonOSTheme
import app.relaxkonos.mobile.ui.theme.ColorMode
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import java.io.File
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class HomeFeedbackFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: HomeViewModel
    private lateinit var session: AuthSession
    private var failRead = false
    private var cancelRead = false
    private var reads = 0
    private var sample = PerformanceSnapshot(1, "2026-10-01T00:00:00Z",
        CpuRealtimeMetrics(12.5, null, null, null, null, emptyList(), null, null, null, null),
        MemoryRealtimeMetrics(1024, 256, 768, null, null, null, null),
        emptyList(), emptyList(), emptyList(), 100, PerformanceHealth(false, null, null))
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private fun percent(value: Double) = context.getString(R.string.home_value_percent, value)
    private fun login() = runBlocking {
        session.login(ServerConnectionIdentityRules.direct("https://home-review.invalid"), "review", "test".toCharArray()) {}
    }
    private var opened: String? = null
    private fun show(fontScale: Float = 1.6f) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.METRICS, ServerCapabilities.FILES, ServerCapabilities.DOCKER, ServerCapabilities.WEB_SERVER, ServerCapabilities.APPLICATION_DEPLOYMENTS, ServerCapabilities.TUNNELS, ServerCapabilities.PROXY, ServerCapabilities.TERMINAL, ServerCapabilities.CERTIFICATES, ServerCapabilities.FIREWALL, ServerCapabilities.GUARDIAN)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> {
                reads++
                if (cancelRead) throw CancellationException("isolated cancelled read")
                if (failRead) error("private home metrics detail")
                return ApiResult.Success(sample)
            }
        }
        session = AuthSession(gateway); login()
        rule.runOnUiThread {
            model = HomeViewModel(session, SystemRepository(gateway, session), RecentOperationJournal()) { "review connection" }
            store.put("home", model)
        }
        rule.setContent { RelaxKonOSTheme(ColorMode.Light, false) {
            val owner by session.state.collectAsStateWithLifecycle()
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, fontScale)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) {
                    (owner as? SessionState.Active)?.let { HomeContent(model, it, {}, applications = mobileApplications(it.capabilities, it.privilegedOperations) { route -> opened = route }) }
                }
            }
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithContentDescription(text(R.string.common_refresh)).performScrollTo().performClick()
    private fun assertSample(value: Double) = rule.onNodeWithText(percent(value)).performScrollTo().assertIsDisplayed()
    private fun screenshot(name: String) {
        rule.waitForIdle()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        File(context.getExternalFilesDir(null), name).outputStream().use {
            InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }
    @Test fun failedRefreshClearsOldMetricsAndRetryRestoresVisibleReadings() {
        show(); rule.waitUntil(5_000) { model.snapshot != null && !model.loading }
        assertSample(12.5)
        rule.runOnIdle { failRead = true }; refresh()
        rule.waitUntil(5_000) { model.message != null && !model.loading }
        rule.runOnIdle { assertNull(model.snapshot) }
        rule.onNodeWithText("private home metrics detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).assertIsDisplayed()
        rule.runOnIdle { failRead = false }
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { reads == 3 && model.message == null && !model.loading }
        rule.onNodeWithText(text(R.string.common_retry)).assertDoesNotExist()
        assertSample(12.5); screenshot("home-retry-large-font.png")
    }
    @Test fun sameAddressNewLoginAutomaticallyLoadsNewMetricsWithoutManualRefresh() {
        show(); rule.waitUntil(5_000) { model.snapshot != null && !model.loading }
        assertSample(12.5)
        rule.runOnIdle { sample = sample.copy(sequence = 2, cpu = sample.cpu.copy(totalPercent = 50.0)); login() }
        rule.waitUntil(5_000) { reads == 2 && model.snapshot?.cpuPercent == 50.0 && !model.loading }
        rule.onNodeWithText(percent(12.5)).assertDoesNotExist(); assertSample(50.0)
    }
    @Test fun cancelledReadLeavesRefreshReachableAndCanRecover() {
        cancelRead = true; show()
        rule.waitUntil(5_000) { reads == 1 && !model.loading }
        rule.runOnIdle { assertNull(model.snapshot); assertNull(model.message); cancelRead = false }
        rule.onNodeWithContentDescription(text(R.string.common_refresh)).performScrollTo().assertIsEnabled()
        refresh(); rule.waitUntil(5_000) { reads == 2 && model.snapshot != null && !model.loading }
        assertSample(12.5)
    }
    @Test fun desktopRendersRealMetricsAndOpensDocker() {
        show(1f); rule.waitUntil(5_000) { model.snapshot != null && !model.loading }
        rule.onNodeWithText(text(R.string.docker_title)).performScrollTo().assertIsDisplayed()
        screenshot("application-desktop-home.png")
        rule.onNodeWithText(text(R.string.docker_title)).performClick()
        rule.runOnIdle { assertEquals(Routes.MANAGE_DOCKER, opened) }
    }

}
