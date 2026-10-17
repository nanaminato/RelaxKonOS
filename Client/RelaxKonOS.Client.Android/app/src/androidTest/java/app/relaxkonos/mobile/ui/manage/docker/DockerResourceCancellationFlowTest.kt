package app.relaxkonos.mobile.ui.manage.docker

import androidx.activity.ComponentActivity
import android.accessibilityservice.AccessibilityService
import android.content.Intent
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import app.relaxkonos.mobile.ui.common.WorkspaceSection
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
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DockerResourceCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: DockerResourceViewModel
    private var cancelRead = false
    private var writes = 0
    private val active = mutableStateOf(true)
    private var pendingRead: CompletableDeferred<ApiResult<DockerStatus>>? = null
    private val containerId = "a".repeat(64)
    private val statsReads = java.util.concurrent.atomic.AtomicInteger()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun show(includeVolume: Boolean = false, resourceKind: DockerResourceKind = DockerResourceKind.Volumes) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
                pendingRead?.let { return it.await() }
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(DockerStatus(true, "", "28", "linux", "amd64"))
            }
            override suspend fun dockerContainers(serverUrl: String, accessToken: String): ApiResult<List<DockerContainer>> = ApiResult.Success(
                if (resourceKind == DockerResourceKind.Containers) listOf(DockerContainer(containerId, "review-container", "image", "running", "running")) else emptyList())
            override suspend fun dockerContainerDetails(serverUrl: String, accessToken: String, id: String): ApiResult<DockerContainerDetails> =
                ApiResult.Success(DockerContainerDetails(id, "review-container", "image", "", "running", "running", "", "", "",
                    emptyList(), emptyList(), emptyList(), emptyList(), emptyMap()))
            override suspend fun dockerContainerStats(serverUrl: String, accessToken: String, id: String): ApiResult<DockerContainerStats> {
                statsReads.incrementAndGet()
                return ApiResult.Success(DockerContainerStats(id, "0", "0", "0", "0"))
            }
            override suspend fun dockerImages(serverUrl: String, accessToken: String): ApiResult<List<DockerImage>> = ApiResult.Success(emptyList())
            override suspend fun dockerNetworks(serverUrl: String, accessToken: String): ApiResult<List<DockerNetwork>> = ApiResult.Success(emptyList())
            override suspend fun dockerVolumes(serverUrl: String, accessToken: String): ApiResult<List<DockerVolume>> = ApiResult.Success(
                if (includeVolume) listOf(DockerVolume("review-volume", "local", "/review-volume")) else emptyList())
            override suspend fun dockerVolumeDetails(serverUrl: String, accessToken: String, name: String): ApiResult<DockerVolumeDetails> =
                ApiResult.Success(DockerVolumeDetails(name, "local", "/review-volume", emptyList(), mapOf("review-label" to "private-review-value")))
            override suspend fun dockerResourceChange(serverUrl: String, accessToken: String, change: DockerResourceChange): ApiResult<DockerOperation> {
                writes++; throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://docker-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val journal = DockerResourceJournal(Storage())
        val gate = DockerMutationGate(DockerControlJournal(Storage()), journal, DockerComposeJournal(Storage())) { ApiResult.Success(Unit) }
        rule.runOnUiThread {
            model = DockerResourceViewModel(session, DockerResourceRepository(gateway, session, journal, gate), gate)
            store.put("resources", model)
        }
        rule.setContent { MaterialTheme {
            WorkspaceSection(active.value) { DockerResourceContent(model, owner, {}, {}, {}, {}, resourceKind = resourceKind, active = active.value) }
            WorkspaceSection(!active.value) { Text("Other Docker resources") }
        } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    @Test fun backgroundStatsTimerStopsWithoutComposeFramesAndResumes() {
        show(resourceKind = DockerResourceKind.Containers)
        rule.runOnIdle { model.select(DockerResourceKind.Containers, containerId) }
        rule.waitUntil(5_000) { model.state.stats != null && !model.state.busy }
        rule.waitForIdle()
        val activity = rule.activity
        val stage = java.util.concurrent.atomic.AtomicReference(Lifecycle.State.RESUMED)
        rule.runOnUiThread { activity.lifecycle.addObserver(LifecycleEventObserver { owner, _ -> stage.set(owner.lifecycle.currentState) }) }
        val resume = Intent(context, activity.javaClass).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        var before = 0
        rule.mainClock.autoAdvance = false
        try {
            assertTrue(InstrumentationRegistry.getInstrumentation().uiAutomation.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME))
            rule.waitUntil(5_000) { stage.get() == Lifecycle.State.CREATED }
            before = statsReads.get()
            android.os.SystemClock.sleep(3_500)
            assertEquals(before, statsReads.get())
        } finally {
            try { context.startActivity(resume) } finally { rule.mainClock.autoAdvance = true }
        }
        rule.waitUntil(8_000) { stage.get() == Lifecycle.State.RESUMED && statsReads.get() > before && !model.state.busy }
        rule.runOnIdle { assertEquals(containerId, model.state.target?.id); assertNull(model.state.problem); assertEquals(0, writes) }
    }
    @Test fun hiddenResourcePageWithdrawsConfirmationAndPreservesVolumeDraft() {
        show()
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().performClick()
        rule.onNode(hasSetTextAction() and hasText(text(R.string.docker_resources_name))).performScrollTo().performTextInput("review-data")
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val old = requireNotNull(model.confirmation)
        rule.runOnIdle { active.value = false }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        rule.runOnIdle { assertNull(model.confirmation); model.change(old); assertEquals(0, writes) }
        rule.runOnIdle { active.value = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("review-data").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.common_cancel)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { active.value = false }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { active.value = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("review-data").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    private fun pauseAndResume() {
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
    }
    @Test fun pausedResourceConfirmationRequiresNewReviewAndKeepsDraft() {
        show()
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().performClick()
        rule.onNode(hasSetTextAction() and hasText(text(R.string.docker_resources_name))).performScrollTo().performTextInput("review-data")
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val old = requireNotNull(model.confirmation)
        pauseAndResume()
        rule.runOnIdle { assertNull(model.confirmation); model.change(old); assertEquals(0, writes) }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("review-data").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.docker_resources_apply)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.runOnIdle { model.change(old); assertEquals(1, writes) }
    }
    @Test fun hiddenReadFailureDoesNotCoverOtherResourcePageAndStillRequiresRefresh() {
        show()
        val response = CompletableDeferred<ApiResult<DockerStatus>>()
        rule.runOnIdle { pendingRead = response; model.refresh() }
        rule.waitUntil(5_000) { model.state.busy }
        rule.runOnIdle { active.value = false; response.complete(ApiResult.Transport(null)) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.problem != null }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        rule.runOnIdle { active.value = true }
        dismissFailure()
        rule.runOnIdle { pendingRead = null }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun hiddenAndPausedResourceDetailsRemaskSensitiveLabels() {
        show(includeVolume = true)
        rule.runOnIdle { model.select(DockerResourceKind.Volumes, "review-volume") }
        rule.waitUntil(5_000) { !model.state.busy && model.state.target != null }
        rule.onNodeWithText(text(R.string.docker_resources_show_sensitive)).performScrollTo().performClick()
        rule.onNodeWithText("review-label=private-review-value").performScrollTo().assertIsDisplayed()
        pauseAndResume()
        rule.onNodeWithText("review-label=private-review-value").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.docker_resources_show_sensitive)).performScrollTo().performClick()
        rule.onNodeWithText("review-label=private-review-value").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { active.value = false }
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        rule.runOnIdle { active.value = true }
        rule.onNodeWithText("review-label=private-review-value").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.docker_resources_show_sensitive)).performScrollTo().performClick()
        rule.onNodeWithText("review-label=private-review-value").performScrollTo().assertIsDisplayed()
        pauseAndResume()
        rule.onNodeWithText("review-label=private-review-value").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.docker_resources_show_sensitive)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.docker_control_unverified)).assertIsDisplayed()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_resources_create_volume))).assertDoesNotExist()
    }
    @Test fun cancelledRefreshRemovesActionsUntilExplicitRecovery() {
        show()
        rule.runOnIdle { cancelRead = true }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        dismissFailure()
        rule.runOnIdle { cancelRead = false }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledCreationKeepsMarkerAndRefreshDoesNotReplay() {
        show()
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().performClick()
        rule.onNode(hasSetTextAction() and hasText(text(R.string.docker_resources_name))).performScrollTo().performTextInput("review-data")
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.docker_resources_apply)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_resources_discard))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
}
