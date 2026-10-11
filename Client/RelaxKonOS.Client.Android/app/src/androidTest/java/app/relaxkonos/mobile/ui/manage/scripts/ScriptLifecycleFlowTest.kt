package app.relaxkonos.mobile.ui.manage.scripts

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import app.relaxkonos.mobile.R
import java.io.File
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
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
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ScriptsViewModel
    private val reads = AtomicInteger()
    private val blocked = AtomicBoolean()
    private val finished = AtomicBoolean()
    private val writes = AtomicInteger()
    private val empty = AtomicBoolean()
    private val failList = AtomicBoolean()
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
                if (count == 2) { blocked.set(true); awaitCancellation() }
                return ApiResult.Success(ScriptTaskResult(true, "", task(if (finished.get()) "succeeded" else "running")))
            }
            override suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> {
                writes.incrementAndGet(); return ApiResult.Transport(null)
            }
            override suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> {
                writes.incrementAndGet(); return ApiResult.Transport(null)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://script-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = Unit
        })
        val journal = ScriptRequestJournal(object : InstallationRequestStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread {
            model = ScriptsViewModel(session, ScriptTaskRepository(gateway, session, index), journal)
            store.put("scripts", model)
        }
        rule.setContent { MaterialTheme { ScriptsContent(model, owner, {}) } }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.listVerified }
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
        rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
        val before = reads.get()
        android.os.SystemClock.sleep(2_300)
        assertEquals(before, reads.get())
        assertFalse(model.state.value.error)
        assertEquals("running", model.state.value.selected?.state)
        finished.set(true)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(6_000) { model.state.value.selected?.state == "succeeded" }
        rule.runOnIdle { assertFalse(model.state.value.error); assertEquals(0, writes.get()) }
    }
}
