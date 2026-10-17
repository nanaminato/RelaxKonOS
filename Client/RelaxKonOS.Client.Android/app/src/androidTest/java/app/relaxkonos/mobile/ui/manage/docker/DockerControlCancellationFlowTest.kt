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
import androidx.compose.runtime.CompositionLocalProvider
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DockerControlCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: DockerControlViewModel
    private var cancelRead = false
    private var writes = 0
    private val section = mutableStateOf("overview")
    private var pendingRead: CompletableDeferred<ApiResult<DockerStatus>>? = null
    private val operationReads = java.util.concurrent.atomic.AtomicInteger()
    private var terminalOperation = false
    private val operation = InstallationOperation("11111111-1111-1111-1111-111111111111", InstallationService.Docker,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
    private val tasks = mutableMapOf(operation.operationId to operation)
    private val cancellations = mutableListOf<String>()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val facts = DockerControlFacts(DockerStatus(true, "", "28", "linux", "amd64"), emptyList())
    private fun show(includeMirrors: Boolean = false, includeRecords: Boolean = false) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
                pendingRead?.let { return it.await() }
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(facts.status)
            }
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(null)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                operationReads.incrementAndGet()
                val task = tasks[operationId] ?: return ApiResult.Problem(404, "installation.missing", null)
                return ApiResult.Success(if (terminalOperation) task.copy(state = InstallationState.Succeeded, stage = InstallationStage.Completed, completedAtMillis = 2) else task)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                cancellations += operationId
                return ApiResult.Success(requireNotNull(tasks[operationId]).copy(state = InstallationState.Cancelled, stage = InstallationStage.Cancelled, completedAtMillis = 2))
            }
            override suspend fun dockerMirrors(serverUrl: String, accessToken: String): ApiResult<List<DockerImageMirror>> = ApiResult.Success(emptyList())
            override suspend fun dockerEngineAction(serverUrl: String, accessToken: String, action: DockerEngineAction, confirmed: Boolean): ApiResult<DockerEngineResult> {
                writes++; throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://docker-control-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        val controlJournal = DockerControlJournal(Storage())
        val resourceJournal = DockerResourceJournal(Storage())
        val gate = DockerMutationGate(controlJournal, resourceJournal, DockerComposeJournal(Storage())) { ApiResult.Success(Unit) }
        val index = OperationIndex(object : OperationIndexStorage {
            private var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread {
            model = DockerControlViewModel(session, DockerControlRepository(gateway, session, controlJournal, gate),
                DockerResourceRepository(gateway, session, resourceJournal, gate),
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage())), index, gate, ElevationAnswerProvider.Declines)
            store.put("firewall", model)
        }
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides app.container) { MaterialTheme {
            WorkspaceSection(section.value == "overview") { DockerControlContent(model, owner, {}, active = section.value == "overview") }
            if (includeMirrors) WorkspaceSection(section.value == "mirrors") { DockerControlContent(model, owner, {}, mirrorsOnly = true, active = section.value == "mirrors") }
            if (includeRecords) WorkspaceSection(section.value == "records") { DockerControlContent(model, owner, {}, recordsOnly = true, active = section.value == "records") }
            WorkspaceSection(section.value == "other") { Text("Other Docker resources") }
        } } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    @Test fun replacingInstallationWithdrawsOldCancelAndNewConfirmationTargetsNewTask() {
        show(includeRecords = true)
        val next = operation.copy(operationId = "22222222-2222-2222-2222-222222222222")
        rule.runOnIdle { tasks[next.operationId] = next; section.value = "records"; model.recoverInstall(operation.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.operationId == operation.operationId }
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { model.recoverInstall(next.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.operationId == next.operationId }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertTrue(cancellations.isEmpty()) }
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.state == InstallationState.Cancelled }
        rule.runOnIdle { assertEquals(listOf(next.operationId), cancellations); assertEquals(0, writes) }
    }
    @Test fun hiddenEngineConfirmationIsWithdrawnAndNeedsFreshAction() {
        show()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val old = requireNotNull(model.confirmation)
        rule.runOnIdle { section.value = "other" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        rule.runOnIdle { assertNull(model.confirmation); model.change(old); assertEquals(0, writes) }
        rule.runOnIdle { section.value = "overview" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasAnyAncestor(isDialog())).performClick()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun pausedEngineConfirmationRejectsOldCallbackAndFreshReviewSubmitsOnce() {
        show()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        val old = requireNotNull(model.confirmation)
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { assertNull(model.confirmation); model.change(old); assertEquals(0, writes) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.docker_control_confirm)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.runOnIdle { model.change(old); assertEquals(1, writes) }
    }
    @Test fun sharedControlFailureHasNoHiddenDialogsAndOnlyOneVisibleFeedback() {
        show(includeMirrors = true)
        rule.runOnIdle { section.value = "mirrors" }
        rule.onNodeWithText(text(R.string.docker_control_add_mirror)).performScrollTo().assertIsEnabled()
        val response = CompletableDeferred<ApiResult<DockerStatus>>()
        rule.runOnIdle { pendingRead = response; model.refresh() }
        rule.waitUntil(5_000) { model.state.busy }
        rule.runOnIdle { section.value = "other"; response.complete(ApiResult.Transport(null)) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.problem != null }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        rule.runOnIdle { section.value = "mirrors" }
        rule.onAllNodes(isDialog()).assertCountEquals(1)
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.runOnIdle { assertNull(model.state.facts); pendingRead = null }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_control_add_mirror)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    private fun pauseAndResume() {
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
    }
    @Test fun mirrorDraftSurvivesPauseAndHidingButConfirmationsDoNot() {
        show(includeMirrors = true)
        rule.runOnIdle { section.value = "mirrors" }
        rule.onNodeWithText(text(R.string.docker_control_add_mirror)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.docker_control_mirror_name)).performScrollTo().performTextInput("review-mirror")
        rule.onNodeWithText(text(R.string.docker_control_mirror_endpoint)).performScrollTo().performTextInput("registry.example.test")
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        pauseAndResume()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("review-mirror").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().performClick()
        rule.runOnIdle { section.value = "other" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { section.value = "mirrors" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("registry.example.test").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { section.value = "other" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { section.value = "mirrors" }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("review-mirror").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun hiddenSharedControlStopsScheduledObservationAndResumesOriginalTask() {
        show(includeMirrors = true)
        rule.runOnIdle { section.value = "mirrors" }
        rule.onNodeWithText(text(R.string.docker_control_add_mirror)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { model.recoverInstall(operation.operationId, false) }
        rule.waitUntil(5_000) { operationReads.get() >= 1 && !model.state.busy }
        rule.runOnIdle { section.value = "other" }
        rule.onNodeWithText("Other Docker resources").assertIsDisplayed()
        val before = operationReads.get()
        android.os.SystemClock.sleep(2_000)
        assertEquals(before, operationReads.get())
        rule.runOnIdle { terminalOperation = true; section.value = "overview" }
        rule.waitUntil(5_000) { model.state.installation?.state == InstallationState.Succeeded && !model.state.busy }
        rule.runOnIdle { assertEquals(operation.operationId, model.state.installation?.operationId); assertEquals(0, writes) }
    }
    @Test fun backgroundInstallationTimerStopsWithoutComposeFramesAndResumes() {
        show()
        rule.runOnIdle { model.recoverInstall(operation.operationId, false) }
        rule.waitUntil(5_000) { operationReads.get() >= 1 && !model.state.busy }
        rule.waitForIdle()
        val activity = rule.activity
        val stage = java.util.concurrent.atomic.AtomicReference(Lifecycle.State.RESUMED)
        rule.runOnUiThread { activity.lifecycle.addObserver(LifecycleEventObserver { owner, _ -> stage.set(owner.lifecycle.currentState) }) }
        val resume = Intent(context, activity.javaClass).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        rule.mainClock.autoAdvance = false
        try {
            assertTrue(InstrumentationRegistry.getInstrumentation().uiAutomation.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME))
            rule.waitUntil(5_000) { stage.get() == Lifecycle.State.CREATED }
            val before = operationReads.get()
            android.os.SystemClock.sleep(2_000)
            assertEquals(before, operationReads.get())
            terminalOperation = true
        } finally {
            try { context.startActivity(resume) } finally { rule.mainClock.autoAdvance = true }
        }
        rule.waitUntil(8_000) { stage.get() == Lifecycle.State.RESUMED && model.state.installation?.state == InstallationState.Succeeded && !model.state.busy }
        rule.runOnIdle { assertEquals(operation.operationId, model.state.installation?.operationId); assertEquals(0, writes); assertTrue(cancellations.isEmpty()) }
    }
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.docker_control_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).assertDoesNotExist()
    }
    @Test fun cancelledRefreshRemovesOldActionsAndExplicitRefreshRecovers() {
        show()
        rule.runOnIdle { cancelRead = true }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        dismissFailure()
        rule.runOnIdle { cancelRead = false }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledChangeRetainsMarkerAndRefreshDoesNotReplayWrite() {
        show()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_control_confirm))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
}

