package app.relaxkonos.mobile.ui.manage.scripts

import androidx.activity.ComponentActivity
import android.content.Intent
import android.accessibilityservice.AccessibilityService
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import app.relaxkonos.mobile.R
import java.io.File
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicBoolean
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ScriptLifecycleFlowTest {
    @get:Rule(order = 0) val diagnostics = app.relaxkonos.mobile.testing.ReviewWaitDiagnostics()
    @get:Rule(order = 1) val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ScriptsViewModel
    private lateinit var owner: SessionState.Active
    private var blockSecondRead = true
    private val cancelledIds = mutableListOf<String>()
    private val reads = AtomicInteger()
    private val blocked = AtomicBoolean()
    private val finished = AtomicBoolean()
    private val writes = AtomicInteger()
    private val submittedRequests = mutableListOf<ScriptRequest>()
    private val empty = AtomicBoolean()
    private val failList = AtomicBoolean()
    private val failJournal = AtomicBoolean()
    private var heldDetails: CompletableDeferred<ApiResult<ScriptTaskResult>>? = null
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun task(state: String = "running") = ScriptTask("review-task", "/bin/true", "review", state,
        "date", null, null, null, emptyList(), false)

    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", emptySet(), false),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "review-workspace"))
            override suspend fun scriptTasks(serverUrl: String, accessToken: String): ApiResult<ScriptTasksResult> =
                if (failList.get()) ApiResult.Transport(null) else ApiResult.Success(ScriptTasksResult(true, "", if (empty.get()) emptyList() else listOf(task())))
            override suspend fun scriptTask(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> {
                val count = reads.incrementAndGet()
                heldDetails?.let { return it.await() }
                if (count == 2 && blockSecondRead) { blocked.set(true); awaitCancellation() }
                return ApiResult.Success(ScriptTaskResult(true, "", task(if (finished.get()) "succeeded" else "running").copy(id = id)))
            }
            override suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> {
                submittedRequests += request
                writes.incrementAndGet(); return ApiResult.Transport(null)
            }
            override suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> {
                cancelledIds += id
                writes.incrementAndGet(); return ApiResult.Transport(null)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://script-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = Unit
        })
        val journal = ScriptRequestJournal(object : InstallationRequestStorage {
            var bytes: ByteArray? = null
            override fun read(): ByteArray? { check(!failJournal.get()); return bytes }
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread {
            model = ScriptsViewModel(session, ScriptTaskRepository(gateway, session, index), journal)
            store.put("scripts", model)
        }
        rule.setContent { MaterialTheme { ScriptsContent(model, owner, {}) } }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.listVerified }
    }

    private fun clickAction(node: SemanticsNodeInteraction) = requireNotNull(node.fetchSemanticsNode()
        .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action)

    @Test fun repeatedImmediateListFailureReopensDismissedFeedback() {
        blockSecondRead = false; show()
        rule.runOnIdle { failList.set(true); model.load(owner) }
        rule.onNodeWithText(text(R.string.scripts_failed)).assertIsDisplayed()
        val dismiss = clickAction(rule.onNodeWithText(text(R.string.common_dismiss)))
        rule.runOnIdle { dismiss(); model.load(owner) }
        rule.onNodeWithText(text(R.string.scripts_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.runOnIdle { assertEquals(0, writes.get()); assertFalse(model.state.value.loading); failList.set(false) }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.listVerified && !model.state.value.loading }
        rule.onNode(isDialog()).assertDoesNotExist()
    }

    @Test fun repeatedJournalFailureReopensFeedbackAndCorrectionRestoresNewTask() {
        blockSecondRead = false; show()
        rule.runOnIdle { failJournal.set(true); model.load(owner) }
        rule.onNodeWithText(text(R.string.scripts_storage_failed)).assertIsDisplayed()
        val dismiss = clickAction(rule.onNodeWithText(text(R.string.common_dismiss)))
        rule.runOnIdle { dismiss(); model.load(owner) }
        rule.onNodeWithText(text(R.string.scripts_storage_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(0, writes.get()); assertFalse(model.state.value.loading); failJournal.set(false) }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.journalAvailable && model.state.value.listVerified && !model.state.value.loading }
        rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo().assertIsEnabled()
    }

    @Test fun slowDetailRetryDoesNotReopenOldErrorBeforeItsOutcome() {
        blockSecondRead = false; show()
        rule.runOnIdle { failList.set(true); model.load(owner) }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        val response = CompletableDeferred<ApiResult<ScriptTaskResult>>()
        rule.runOnIdle { heldDetails = response; model.select("review-task") }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, writes.get()); response.complete(ApiResult.Transport(null)) }
        rule.onNodeWithText(text(R.string.scripts_failed)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, reads.get()); assertEquals(0, writes.get()) }
    }

    private fun fillEditor(executable: String) {
        rule.onNodeWithText(text(R.string.guardian_executable)).performScrollTo().performTextInput(executable)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.guardian_directory)).performScrollTo().performTextInput("/tmp")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
    }

    @Test fun oldNewEntryCannotOpenEditorAfterPauseAndResume() {
        blockSecondRead = false; show()
        val oldOpen = clickAction(rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo())
        val checkedResume = AtomicBoolean()
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) { oldOpen(); assertNull(model.state.value.draft); checkedResume.set(true) }
        }
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { oldOpen(); assertNull(model.state.value.draft); rule.activity.lifecycle.addObserver(observer) }
            rule.mainClock.autoAdvance = true
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { checkedResume.get() }
        } finally {
            rule.mainClock.autoAdvance = true
            rule.runOnUiThread { rule.activity.lifecycle.removeObserver(observer) }
        }
        rule.runOnIdle { oldOpen(); assertNull(model.state.value.draft); assertEquals(0, writes.get()) }
        rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo().performClick()
        rule.runOnIdle { assertNotNull(model.state.value.draft); assertEquals(0, writes.get()) }
    }

    @Test fun editorResumeRejectsOldCallbacksAndUnknownRunKeepsOriginalDraft() {
        blockSecondRead = false; show()
        val oldOpen = clickAction(rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo())
        rule.onNodeWithText(text(R.string.scripts_new)).performClick()
        fillEditor("/bin/review-current")
        val draft = requireNotNull(model.state.value.draft)
        val oldField = requireNotNull(rule.onNode(hasText(text(R.string.guardian_executable)) and hasSetTextAction())
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.SetText].action)
        val oldRun = clickAction(rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo())
        val oldBack = clickAction(rule.onNodeWithContentDescription(text(R.string.common_back)).performScrollTo())
        val checkedResume = AtomicBoolean()
        fun staleCallbacks() {
            oldField(androidx.compose.ui.text.AnnotatedString("/stale")); oldRun(); oldBack(); oldOpen()
            assertSame(draft, model.state.value.draft); assertEquals(0, writes.get())
        }
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) { staleCallbacks(); checkedResume.set(true) }
        }
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { staleCallbacks(); rule.activity.lifecycle.addObserver(observer) }
            rule.mainClock.autoAdvance = true
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { checkedResume.get() }
        } finally {
            rule.mainClock.autoAdvance = true
            rule.runOnUiThread { rule.activity.lifecycle.removeObserver(observer) }
        }
        rule.runOnIdle { staleCallbacks() }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("/bin/review-current").assertExists()
        val current = clickAction(rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo())
        rule.runOnIdle { current(); current() }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.pending != null }
        rule.onNodeWithText(text(R.string.scripts_write_unknown)).assertExists()
        rule.onNodeWithText(text(R.string.scripts_run)).assertIsNotEnabled()
        rule.runOnIdle {
            assertEquals(listOf("/bin/review-current"), submittedRequests.map { it.executablePath })
            assertEquals(listOf("/tmp"), submittedRequests.map { it.workingDirectory })
            assertEquals(1, writes.get()); assertSame(draft, model.state.value.draft)
        }
    }

    @Test fun discardedEditorCallbacksCannotAffectReopenedDraft() {
        blockSecondRead = false; show()
        rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo().performClick()
        fillEditor("/bin/old")
        val oldField = requireNotNull(rule.onNode(hasText(text(R.string.guardian_executable)) and hasSetTextAction())
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.SetText].action)
        val oldRun = clickAction(rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo())
        val oldBack = clickAction(rule.onNodeWithContentDescription(text(R.string.common_back)).performScrollTo())
        rule.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.onNodeWithText(text(R.string.scripts_new)).performScrollTo().performClick()
        val current = requireNotNull(model.state.value.draft)
        rule.runOnIdle {
            oldField(androidx.compose.ui.text.AnnotatedString("/stale")); oldRun(); oldBack()
            assertSame(current, model.state.value.draft); assertEquals(0, writes.get())
        }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText("/stale").assertDoesNotExist()
        fillEditor("/bin/new")
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.pending != null }
        rule.runOnIdle {
            assertEquals(listOf("/bin/new"), submittedRequests.map { it.executablePath })
            assertEquals(1, writes.get())
        }
    }

    @Test fun cancellationPausesObservationAndPauseOrSelectionRequiresNewConfirmation() {
        blockSecondRead = false; show()
        rule.runOnIdle { model.select("review-task") }
        rule.waitUntil(5_000) { model.state.value.selected != null }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val paused = requireNotNull(model.state.value.cancellation)
        rule.onNodeWithText("review-task").assertIsDisplayed()
        val before = reads.get()
        Thread.sleep(2_300)
        rule.runOnIdle { assertEquals(before, reads.get()); assertSame(paused, model.state.value.cancellation) }
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertNull(model.state.value.cancellation); model.cancel(paused); assertEquals(0, writes.get()) }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.mainClock.autoAdvance = true
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val old = requireNotNull(model.state.value.cancellation)
        rule.runOnIdle { model.select("replacement-task") }
        rule.waitUntil(5_000) { model.state.value.selected?.id == "replacement-task" }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancel(old); assertEquals(0, writes.get()) }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        rule.onNodeWithText("replacement-task").assertIsDisplayed()
        val current = requireNotNull(model.state.value.cancellation)
        rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.pending != null }
        rule.runOnIdle {
            model.cancel(current)
            assertEquals(listOf("replacement-task"), cancelledIds)
            assertEquals(1, writes.get())
            assertEquals("replacement-task", model.state.value.pending?.taskId)
        }
    }

    @Test fun oldDismissCallbackCannotWithdrawReopenedCancellation() {
        blockSecondRead = false; show()
        rule.runOnIdle { model.select("review-task") }
        rule.waitUntil(5_000) { model.state.value.selected != null }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val close = rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction())
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performClick()
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val current = requireNotNull(model.state.value.cancellation)
        val confirm = rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction())
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.runOnIdle {
            close()
            assertSame(current, model.state.value.cancellation)
            assertEquals(0, writes.get())
        }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertIsDisplayed()
        rule.runOnIdle { confirm(); confirm() }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.pending != null }
        rule.runOnIdle {
            assertEquals(listOf("review-task"), cancelledIds)
            assertEquals(1, writes.get())
        }
    }

    @Test fun refreshDismissesCancellationWithoutSendingAndNewConfirmationStillWorks() {
        blockSecondRead = false; show()
        rule.runOnIdle { model.select("review-task") }
        rule.waitUntil(5_000) { model.state.value.selected != null }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val old = requireNotNull(model.state.value.cancellation)
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performClick()
        rule.runOnIdle { model.cancel(old); assertEquals(0, writes.get()) }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        val refreshed = requireNotNull(model.state.value.cancellation)
        rule.runOnIdle { model.load(refreshed.owner) }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.selected != null }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancel(refreshed); assertEquals(0, writes.get()) }
        rule.onNodeWithText(text(R.string.scripts_cancel)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.pending != null }
        rule.runOnIdle { assertEquals(listOf("review-task"), cancelledIds) }
    }

    @Test fun emptyListFeedbackWithdrawsOnFailureAndActualRefreshRestoresIt() {
        empty.set(true); show()
        rule.onNodeWithText(text(R.string.scripts_empty)).assertIsDisplayed()
        val bitmap = InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
        File(context.getExternalFilesDir(null), "script-empty-review.png").outputStream().use {
            assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it))
        }
        bitmap.recycle()
        failList.set(true)
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.error }
        rule.onNodeWithText(text(R.string.scripts_empty)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.scripts_empty)).assertDoesNotExist()
        failList.set(false)
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.error }
        rule.onNodeWithText(text(R.string.scripts_empty)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes.get()); assertEquals(0, reads.get()) }
    }

    @Test fun backgroundStopsInflightObservationAndForegroundRestoresCurrentTask() {
        show()
        rule.runOnIdle { model.select("review-task") }
        rule.waitUntil(6_000) { blocked.get() }
        val activity = rule.activity
        val stage = java.util.concurrent.atomic.AtomicReference(activity.lifecycle.currentState)
        val observer = LifecycleEventObserver { owner, _ -> stage.set(owner.lifecycle.currentState) }
        rule.runOnUiThread { activity.lifecycle.addObserver(observer) }
        val resume = Intent(context, activity.javaClass).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or
            Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        rule.mainClock.autoAdvance = false
        try {
            assertTrue(InstrumentationRegistry.getInstrumentation().uiAutomation.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME))
            rule.waitUntil(6_000) { stage.get() == Lifecycle.State.CREATED }
            val before = reads.get()
            android.os.SystemClock.sleep(2_300)
            assertEquals(before, reads.get())
            assertFalse(model.state.value.error)
            assertEquals("running", model.state.value.selected?.state)
            finished.set(true)
        } finally {
            rule.mainClock.autoAdvance = true
            context.startActivity(resume)
        }
        rule.waitUntil(6_000) { stage.get() == Lifecycle.State.RESUMED }
        rule.runOnUiThread { activity.lifecycle.removeObserver(observer) }
        rule.waitUntil(6_000) { model.state.value.selected?.state == "succeeded" }
        rule.runOnIdle { assertFalse(model.state.value.error); assertEquals(0, writes.get()) }
    }
}
