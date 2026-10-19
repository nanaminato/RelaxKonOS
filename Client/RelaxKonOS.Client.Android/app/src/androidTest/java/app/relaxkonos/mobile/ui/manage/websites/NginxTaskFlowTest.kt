package app.relaxkonos.mobile.ui.manage.websites

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import android.graphics.Bitmap
import java.io.File
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

class NginxTaskFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val pollClockTimes = java.util.concurrent.CopyOnWriteArrayList<Long>()
    private val store = ViewModelStore()
    private lateinit var model: NginxViewModel
    private lateinit var owner: SessionState.Active
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Nginx,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private var current = install
    private val web = WebServerOperation("55555555-5555-5555-5555-555555555555", "review-instance", "start",
        WebServerOperationState.Running, "starting", "", null, 1, null)
    private val sent = mutableListOf<String>()
    @Volatile private var reads = 0
    @Volatile private var operationReads = 0
    @Volatile private var installationReads = 0
    private var failedRead = false
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun start() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.WEB_SERVER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun discoverWebServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> = ApiResult.Success(emptyList())
            override suspend fun webServerCandidates(serverUrl: String, accessToken: String): ApiResult<List<WebServerCandidate>> = ApiResult.Success(emptyList())
            override suspend fun hostOperatingSystem(serverUrl: String) = ApiResult.Success(HostOperatingSystemKind.Linux)
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(current)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                installationReads++
                pollClockTimes += rule.mainClock.currentTime; reads++; return if (failedRead) ApiResult.Transport(null) else ApiResult.Success(current)
            }
            override suspend fun webServerOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebServerOperation> {
                operationReads++
                pollClockTimes += rule.mainClock.currentTime; reads++; return ApiResult.Success(web)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                sent += operationId; return ApiResult.Transport(null)
            }
            override suspend fun cancelWebServerOperation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<WebServerOperation> {
                sent += operationId; return ApiResult.Transport(null)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://nginx-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = NginxViewModel(session, gateway,
                WebServerRepository(gateway, session, elevation, index, WebServerRequestJournal(storage())),
                WebPublishingRepository(gateway, session, elevation, index),
                WebSiteRepository(gateway, session, elevation, WebSiteMutationJournal(storage())),
                CertificateRepository(gateway, session, index, CertificateRequestJournal(storage()), elevation),
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(storage())),
                index, ElevationAnswerProvider.Declines, UploadDocumentOpener { null })
            store.put("nginx", model)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Column(Modifier.widthIn(max = 360.dp).fillMaxHeight().statusBarsPadding().verticalScroll(rememberScrollState())) {
                    NginxContent(model, owner, {}, "records", {})
                }
            }
        } }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
    }
    private fun requestButton() = rule.onAllNodesWithText(text(R.string.operations_request_cancel)).filter(hasClickAction()).onFirst()
    @Test fun taskReplacementWithdrawsOldConfirmationAndButtonCancelsOnlyNewTask() {
        start()
        requestButton().performScrollTo().performClick()
        val old = requireNotNull(model.cancellation)
        rule.onNodeWithText(install.operationId).assertIsDisplayed()
        rule.runOnUiThread {
            current = install.copy(operationId = "66666666-6666-6666-6666-666666666666")
            model.refresh()
        }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancel(old); assertTrue(sent.isEmpty()) }
        requestButton().performScrollTo().performClick()
        rule.onNodeWithText(current.operationId).assertIsDisplayed()
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val screenshot = instrumentation.uiAutomation.takeScreenshot()
        try {
            File(instrumentation.targetContext.getExternalFilesDir(null), "nginx-cancellation-review.png").outputStream().use {
                screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally { screenshot.recycle() }
        rule.onNode(hasText(text(R.string.nginx_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.busy && sent.size == 1 }
        rule.runOnIdle { assertEquals(listOf(current.operationId), sent); assertFalse(model.state.installationVerified) }
    }
    @Test fun confirmationPausesObservationAndBackgroundRevokesBothTaskTypes() {
        start()
        for (webTask in listOf(false, true)) {
            if (webTask) {
                rule.runOnUiThread { model.recover(web.operationId) }
                rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
            }
            rule.runOnUiThread { model.requestCancellation(if (webTask) NginxCancellation.Web(owner, web) else NginxCancellation.Installation(owner, current)) }
            val old = requireNotNull(model.cancellation)
            rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertIsDisplayed()
            val before = reads
            Thread.sleep(2_000)
            rule.runOnIdle { assertEquals(before, reads); assertSame(old, model.cancellation) }
            rule.mainClock.autoAdvance = false
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { assertNull(model.cancellation); model.cancel(old); assertTrue(sent.isEmpty()) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.mainClock.autoAdvance = true
            rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        }
    }
    @Test fun failedReadRemovesCancelEntryAndExplicitRefreshRecoversOriginalTask() {
        start()
        rule.runOnUiThread { failedRead = true; model.pollInstallation() }
        rule.waitUntil(5_000) { !model.state.busy && !model.state.installationVerified }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onAllNodesWithText(text(R.string.operations_request_cancel)).assertCountEquals(0)
        rule.runOnUiThread { failedRead = false; model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        requestButton().performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(install.operationId, model.state.installation?.operationId); assertTrue(sent.isEmpty()) }
    }
    @Test fun backgroundStopsAutomaticTaskReadsAndForegroundResumes() {
        start()
        val initial = reads
        rule.waitUntil(8_000) { reads > initial && !model.state.busy }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.waitUntil(5_000) { !model.state.busy }
        val paused = reads
        Thread.sleep(2_000)
        assertEquals(paused, reads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(8_000) { reads > paused && !model.state.busy }
        rule.runOnIdle { assertTrue(sent.isEmpty()) }
    }
    @Test fun identicalSnapshotsContinuePollingWithoutBusyFrames() {
        start()
        rule.runOnUiThread { model.recover(web.operationId) }; rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.waitForIdle()
        val initialOperations = operationReads
        val initialInstallations = installationReads
        rule.waitUntil(10_000) { operationReads >= initialOperations + 3 && installationReads >= initialInstallations + 3 }
        rule.runOnIdle { assertTrue(sent.isEmpty()); assertTrue(model.state.installationVerified) }
    }

    @Test fun pausedTaskObservationRestartsWithFullInterval() {
        start()
        rule.runOnUiThread { model.recover(web.operationId) }; rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.waitForIdle()
        var paused = 0
        var resumedAt = 0L
        rule.mainClock.autoAdvance = false
        try {
            rule.mainClock.advanceTimeBy(1_000)
            rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
            paused = pollClockTimes.size
            rule.mainClock.advanceTimeBy(4_000)
            android.os.SystemClock.sleep(2_000)
            assertEquals(paused, pollClockTimes.size)
            resumedAt = rule.mainClock.currentTime
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.waitUntil(6_000) { pollClockTimes.size > paused }
        val interval = pollClockTimes[paused] - resumedAt
        assertTrue("New observation must wait a full interval, got $interval", interval >= 1500)
        rule.runOnIdle { assertTrue(sent.isEmpty()); assertTrue(model.state.installationVerified) }
    }

}
