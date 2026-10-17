package app.relaxkonos.mobile.ui.manage.git

import android.graphics.Bitmap
import java.io.File
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
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.After
import org.junit.Rule
import org.junit.Test

class GitInstallationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: GitWorkspaceViewModel
    private val operation = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Git,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private var current = operation
    private var cancelRead = false
    @Volatile private var reads = 0
    private val sent = mutableListOf<String>()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun start() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.GIT), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun gitEngine(serverUrl: String, accessToken: String) = ApiResult.Success(GitEngine(false, "not_installed", null, true))
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(current)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                reads++
                if (cancelRead) throw CancellationException("isolated query cancellation")
                return ApiResult.Success(current)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                sent += operationId; return ApiResult.Transport(null)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://git-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = GitWorkspaceViewModel(session, GitRepositoryClient(gateway, session, index),
                GitWorkspaceRepository(gateway, session, GitWorkspaceJournal(storage())) { ApiResult.Success(Unit) },
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(storage())), index, ElevationAnswerProvider.Declines)
            store.put("git", model)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Column(Modifier.widthIn(max = 360.dp).fillMaxHeight().statusBarsPadding().verticalScroll(rememberScrollState())) {
                    GitWorkspaceContent(model, owner, "environment", {})
                }
            }
        } }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
    }
    private fun cancelButton() = rule.onNode(hasText(text(R.string.gw_cancel_install)) and hasClickAction())
    @Test fun pauseRevokesConfirmationAndReplacementButtonCancelsOnlyNewTask() {
        start(); cancelButton().performScrollTo().performClick()
        val paused = requireNotNull(model.installationCancellation)
        rule.onNodeWithText(operation.operationId).assertIsDisplayed()
        val before = reads
        Thread.sleep(2_800)
        rule.runOnIdle { assertEquals(before, reads); assertSame(paused, model.installationCancellation) }
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertNull(model.installationCancellation); model.cancelInstall(paused); assertTrue(sent.isEmpty()) }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.mainClock.autoAdvance = true
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        cancelButton().performScrollTo().performClick()
        val old = requireNotNull(model.installationCancellation)
        rule.runOnUiThread { current = operation.copy(operationId = "66666666-6666-6666-6666-666666666666"); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancelInstall(old); assertTrue(sent.isEmpty()) }
        cancelButton().performScrollTo().performClick()
        rule.onNodeWithText(current.operationId).assertIsDisplayed()
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val screenshot = instrumentation.uiAutomation.takeScreenshot()
        try {
            File(instrumentation.targetContext.getExternalFilesDir(null), "git-installation-cancellation-review.png").outputStream().use {
                screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally { screenshot.recycle() }
        rule.onNode(hasText(text(R.string.gw_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.busy && sent.size == 1 }
        rule.runOnIdle { assertEquals(listOf(current.operationId), sent); assertFalse(model.state.installationVerified) }
    }
    @Test fun backgroundStopsTaskObservationAndForegroundResumes() {
        start(); val initial = reads
        rule.waitUntil(8_000) { reads > initial && !model.state.busy }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle(); rule.waitUntil(5_000) { !model.state.busy }
        val paused = reads
        Thread.sleep(2_800)
        assertEquals(paused, reads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(8_000) { reads > paused && !model.state.busy }
    }
    @Test fun cancelledReadDisablesCancelUntilExplicitRefreshRestoresOriginalTask() {
        start()
        rule.runOnUiThread { cancelRead = true; model.pollInstall() }
        rule.waitUntil(5_000) { !model.state.busy && !model.state.installationVerified }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.gw_install_unverified)).assertExists()
        cancelButton().performScrollTo().assertIsNotEnabled()
        rule.runOnUiThread { cancelRead = false; model.pollInstall() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        cancelButton().performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(operation.operationId, model.state.installation?.operationId); assertTrue(sent.isEmpty()) }
    }
}
