package app.relaxkonos.mobile.ui.manage.git

import android.accessibilityservice.AccessibilityServiceInfo
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import android.view.accessibility.AccessibilityWindowInfo
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import java.io.File
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class GitConflictFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private val id = "11111111-1111-1111-1111-111111111111"
    private val sha = "a".repeat(40)
    private val file = GitConflictFile("a.txt", "b".repeat(64), "base", "ours", "theirs", "resolution result", true)
    private var onConflict: suspend () -> ApiResult<GitConflictFile> = { ApiResult.Success(file) }
    private val status = GitStatus("main", emptyList(), emptyList(), emptyList(), emptyList(), 0, 0, "origin/main", false, "e".repeat(64))
    private var onStatus: suspend () -> ApiResult<GitStatus> = { ApiResult.Success(status) }
    private var conflictPaths = listOf("a.txt")
    private var onMutation: suspend () -> ApiResult<GitOperation> = { ApiResult.Transport(null) }
    private var sends = 0
    private lateinit var model: GitWorkspaceViewModel
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private class IndexStorage : OperationIndexStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun showConflict() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.GIT)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = id))
            override suspend fun gitEngine(serverUrl: String, accessToken: String) = ApiResult.Success(GitEngine(true, "", "2", false))
            override suspend fun gitRepositories(serverUrl: String, accessToken: String) = ApiResult.Success(listOf(GitRepository(id, "Review", "/repo", "main")))
            override suspend fun gitStatus(serverUrl: String, accessToken: String, id: String) = onStatus()
            override suspend fun gitBranches(serverUrl: String, accessToken: String, id: String) =
                ApiResult.Success(listOf(GitBranch("main", sha, true, false, "origin/main", 0, 0)))
            override suspend fun gitConflicts(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(GitConflictState("merge", conflictPaths))
            override suspend fun gitLog(serverUrl: String, accessToken: String, id: String, skip: Int, search: String) =
                ApiResult.Success(listOf(GitCommit(sha, sha.take(7), "author", "date", "subject")))
            override suspend fun gitConflict(serverUrl: String, accessToken: String, id: String, path: String) = onConflict()
            override suspend fun gitMutation(serverUrl: String, accessToken: String, id: String, change: GitMutation): ApiResult<GitOperation> {
                sends++; return onMutation()
            }
        }
        val session = AuthSession(gateway)
        val index = OperationIndex(IndexStorage())
        val workspace = GitWorkspaceRepository(gateway, session, GitWorkspaceJournal(Storage())) { ApiResult.Success(Unit) }
        val installations = InstallationRepository(gateway, session,
            ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), app.container.keyManager),
                UsageMemoryStore(InMemoryUsageMemoryStorage())), index, InstallationRequestJournal(Storage()))
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://git-review.invalid"), "review", "test".toCharArray()) {} }
        rule.runOnUiThread {
            rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            model = GitWorkspaceViewModel(session, GitRepositoryClient(gateway, session, index), workspace, installations, index, ElevationAnswerProvider.Declines)
            store.put("git", model)
        }
        rule.waitUntil(5_000) { model.state.owner != null }
        rule.runOnIdle { model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.runOnIdle { model.conflict(file.path) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.conflict != null }
        rule.setContent { MaterialTheme {
            val state = model.state
            state.conflict?.let { GitConflictDialog(it, model,
                state.owner?.executionEligibility?.available == true && !state.busy && state.facts != null && state.pending.isEmpty() && !state.pendingInstallation && state.installation?.state?.active != true) }
                ?: Text("Parent page")
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }

    private fun confirmAdoption() = rule.onNode(hasText(text(R.string.gw_adopt)) and hasClickAction() and
        hasAnyAncestor(isDialog() and hasAnyDescendant(hasText(text(R.string.gw_adopt_note))))).performClick()

    private fun screenshot(name: String) {
        rule.waitForIdle()
        // Platform dialog fades are outside Compose idling; capture the settled visual state.
        android.os.SystemClock.sleep(500)
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val evidence = File(instrumentation.targetContext.getExternalFilesDir(null), name)
        evidence.outputStream().use { instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it) }
    }

    private fun tapVisibleDialogAction(label: Int) {
        val node = rule.onNode(hasText(text(label)) and hasClickAction()).assertIsDisplayed()
        val bounds = node.fetchSemanticsNode().boundsInWindow
        val origin = IntArray(2)
        Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
            .inRoot(object : org.hamcrest.TypeSafeMatcher<androidx.test.espresso.Root>() {
                override fun describeTo(description: org.hamcrest.Description) { description.appendText("focused Git dialog") }
                override fun matchesSafely(root: androidx.test.espresso.Root) =
                    androidx.test.espresso.matcher.RootMatchers.isDialog().matches(root) && root.decorView.hasWindowFocus()
            }).check { view, failure ->
                if (failure != null) throw failure
                val inWindow = IntArray(2)
                view.getLocationOnScreen(origin); view.getLocationInWindow(inWindow)
                origin[0] -= inWindow[0]; origin[1] -= inWindow[1]
            }
        val screen = Rect(bounds.left.toInt() + origin[0], bounds.top.toInt() + origin[1],
            bounds.right.toInt() + origin[0], bounds.bottom.toInt() + origin[1])
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val display = Rect().also { rule.activity.window.decorView.getWindowVisibleDisplayFrame(it) }
        val keyboard = automation.windows.firstOrNull { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
            ?.let { Rect().also(it::getBoundsInScreen) }
        assertFalse(screen.isEmpty)
        assertTrue("Git action $screen must be within $display and above $keyboard",
            screen.left >= display.left && screen.right <= display.right && screen.top >= display.top &&
                screen.bottom <= (keyboard?.top ?: display.bottom))
        val down = SystemClock.uptimeMillis()
        listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP).forEach { action ->
            val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action, screen.exactCenterX(), screen.exactCenterY(), 0)
            try { event.source = InputDevice.SOURCE_TOUCHSCREEN; assertTrue(automation.injectInputEvent(event, true)) }
            finally { event.recycle() }
        }
    }

    @Test fun keyboardPreviewCancelBackAndConfirmPreserveTargetAndSendOnlyOnce() {
        showConflict()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val originalFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            rule.onNode(hasSetTextAction() and hasText("resolution result")).performScrollTo().performTextReplacement("keyboard resolution")
            fun previewFromKeyboard() {
                rule.onNode(hasSetTextAction() and hasText("keyboard resolution")).performScrollTo().performClick()
                rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
                tapVisibleDialogAction(R.string.gw_choose_edited)
                rule.waitUntil(5_000) { !model.state.busy && model.state.preview != null }
                rule.waitForIdle()
                // The preview model can be ready before its platform Dialog has focus.
                SystemClock.sleep(500)
                rule.runOnIdle {
                    assertEquals(id, model.state.preview?.facts?.repositoryId)
                    assertEquals(file, model.state.preview?.change?.conflict)
                    assertEquals("keyboard resolution", model.state.preview?.change?.content)
                    assertEquals(0, sends)
                }
            }
            previewFromKeyboard()
            screenshot("git-conflict-keyboard-preview.png")
            tapVisibleDialogAction(R.string.common_cancel)
            rule.waitUntil(5_000) { model.state.preview == null }
            rule.runOnIdle { assertEquals("keyboard resolution", model.conflictDraft.text); assertEquals(file, model.state.conflict) }
            previewFromKeyboard()
            Espresso.pressBack()
            rule.waitUntil(5_000) { model.state.preview == null }
            rule.onNodeWithText(text(R.string.editor_unsaved)).assertDoesNotExist()
            rule.runOnIdle { assertEquals("keyboard resolution", model.conflictDraft.text); assertEquals(0, sends) }
            val receipt = CompletableDeferred<ApiResult<GitOperation>>()
            rule.runOnIdle { onMutation = { receipt.await() } }
            previewFromKeyboard()
            tapVisibleDialogAction(R.string.gw_confirm)
            rule.waitUntil(5_000) { model.state.busy && sends == 1 }
            Espresso.pressBack()
            rule.runOnIdle { assertEquals(file, model.state.conflict); assertEquals("keyboard resolution", model.conflictDraft.text); assertNull(model.state.preview) }
            rule.runOnIdle { conflictPaths = emptyList() }
            receipt.complete(ApiResult.Success(GitOperation(true, "resolve", false, emptyList())))
            rule.waitUntil(5_000) { !model.state.busy && model.state.conflict == null }
            rule.onNodeWithText("Parent page").assertIsDisplayed()
            rule.runOnIdle { assertTrue(model.state.pending.isEmpty()); assertTrue(model.state.saved); assertEquals("", model.conflictDraft.text); assertEquals(1, sends) }
        } finally {
            info.flags = originalFlags
            automation.serviceInfo = info
        }
    }

    @Test fun unknownResolutionCanBeVerifiedInEditorWithoutDiscardOrReplay() {
        showConflict()
        rule.onNode(hasSetTextAction() and hasText("resolution result")).performScrollTo().performTextReplacement("unverified resolution")
        Espresso.closeSoftKeyboard()
        val mutation = CompletableDeferred<ApiResult<GitOperation>>()
        rule.runOnIdle { onMutation = { mutation.await() } }
        rule.onNodeWithText(text(R.string.gw_choose_edited)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.preview != null }
        rule.onNodeWithText(text(R.string.gw_confirm)).performClick()
        rule.waitUntil(5_000) { model.state.busy && sends == 1 }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.gw_conflict_editor)).assertIsDisplayed()
        rule.onNodeWithText("unverified resolution").assertIsNotEnabled()
        mutation.complete(ApiResult.Transport(null))
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.gw_unknown)).assertIsDisplayed()
        screenshot("git-conflict-unknown.png")
        rule.onNodeWithText(text(R.string.gw_choose_edited)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.gw_adopt)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.gw_adopt_note)).assertIsDisplayed()
        screenshot("git-conflict-adoption.png")
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(1, model.state.pending.size); assertEquals("unverified resolution", model.conflictDraft.text); assertEquals(1, sends) }
        rule.runOnIdle { onStatus = { ApiResult.Transport(null) } }
        rule.onNodeWithText(text(R.string.gw_adopt)).performClick()
        confirmAdoption()
        rule.waitUntil(5_000) { !model.state.busy && model.state.problem != null }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.gw_unknown)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, model.state.pending.size); assertEquals("unverified resolution", model.conflictDraft.text); assertEquals(1, sends) }
        val facts = CompletableDeferred<ApiResult<GitStatus>>()
        rule.runOnIdle { onStatus = { facts.await() } }
        rule.onNodeWithText(text(R.string.gw_adopt)).performClick()
        confirmAdoption()
        rule.waitUntil(5_000) { model.state.busy }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.gw_conflict_editor)).assertIsDisplayed()
        facts.complete(ApiResult.Success(status))
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.onNodeWithText(text(R.string.gw_unknown)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.gw_choose_edited)).assertIsEnabled()
        rule.onNodeWithText("unverified resolution").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(id, model.state.selectedId); assertEquals(file, model.state.conflict); assertEquals(1, sends) }
    }

    @Test fun failedReadCanRecoverInsideEditorThenPreviewAndCancelWithoutLosingDraft() {
        showConflict()
        rule.onNode(hasSetTextAction() and hasText("resolution result")).performScrollTo().performTextReplacement("local resolution")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { onConflict = { throw IllegalStateException("private detail") } }
        rule.onNodeWithText(text(R.string.gw_reload_revision)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        rule.onNodeWithText(text(R.string.gw_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.gw_choose_edited)).assertIsNotEnabled()
        val latest = file.copy(revision = "c".repeat(64), theirs = "new theirs")
        val receipt = CompletableDeferred<ApiResult<GitConflictFile>>()
        rule.runOnIdle { onConflict = { receipt.await() } }
        rule.onNodeWithText(text(R.string.gw_reload_revision)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.busy }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.gw_conflict_editor)).assertIsDisplayed()
        rule.onNodeWithText("local resolution").assertIsNotEnabled()
        receipt.complete(ApiResult.Success(latest))
        rule.waitUntil(5_000) { !model.state.busy && model.state.conflict == latest }
        rule.onNodeWithText(text(R.string.gw_choose_edited)).assertIsEnabled().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.preview != null }
        rule.runOnIdle {
            assertEquals(id, model.state.facts?.repositoryId)
            assertEquals("local resolution", model.state.preview?.change?.content)
            assertEquals("c".repeat(64), model.state.preview?.change?.conflict?.revision)
            assertEquals(0, sends)
        }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        // The preview is a platform Dialog; Compose idle alone does not wait for its focus handoff.
        rule.waitForIdle(); SystemClock.sleep(500)
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_unsaved)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.editor_continue_editing)).performClick()
        rule.onNodeWithText("local resolution").performScrollTo().assertIsDisplayed()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
        rule.runOnIdle { assertEquals("", model.conflictDraft.text); assertEquals(0, sends) }
    }

    @Test fun adoptingAlreadyResolvedFactsRetainsDraftAndDisablesObsoleteResolution() {
        showConflict()
        rule.onNode(hasSetTextAction() and hasText("resolution result")).performScrollTo().performTextReplacement("retained resolution")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.gw_choose_edited)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.preview != null }
        rule.onNodeWithText(text(R.string.gw_confirm)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.runOnIdle { conflictPaths = emptyList() }
        rule.onNodeWithText(text(R.string.gw_adopt)).performScrollTo().performClick()
        confirmAdoption()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.onNodeWithText(text(R.string.gw_conflict_no_longer_present)).performScrollTo().assertIsDisplayed()
        screenshot("git-conflict-resolved.png")
        listOf(R.string.gw_choose_ours, R.string.gw_choose_theirs, R.string.gw_choose_delete, R.string.gw_choose_edited).forEach {
            rule.onNodeWithText(text(it)).assertIsNotEnabled()
        }
        rule.onNodeWithText("retained resolution").performScrollTo().assertIsDisplayed()
        rule.runOnIdle {
            assertEquals(id, model.state.selectedId); assertEquals(file, model.state.conflict)
            assertEquals("retained resolution", model.conflictDraft.text); assertEquals(1, sends)
        }
    }

    @Test fun actualKeyboardKeepsConflictHeaderAndResolutionActionsAboveIme() {
        showConflict()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val originalFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            rule.onNode(hasSetTextAction() and hasText("resolution result")).performScrollTo().performClick()
            rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
            rule.waitForIdle()
            val evidence = File(InstrumentationRegistry.getInstrumentation().targetContext.getExternalFilesDir(null), "git-conflict-keyboard.png")
            evidence.outputStream().use { automation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it) }
            val keyboard = automation.windows.single { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
            val keyboardBounds = Rect().also(keyboard::getBoundsInScreen)
            listOf(R.string.gw_conflict_editor, R.string.gw_choose_edited).forEach { label ->
                val node = rule.onNodeWithText(text(label)).assertIsDisplayed()
                val bounds = node.fetchSemanticsNode().boundsInWindow
                val origin = IntArray(2)
                Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
                    .inRoot(androidx.test.espresso.matcher.RootMatchers.isDialog()).check { view, failure ->
                        if (failure != null) throw failure
                        val inWindow = IntArray(2)
                        view.getLocationOnScreen(origin); view.getLocationInWindow(inWindow)
                        origin[0] -= inWindow[0]; origin[1] -= inWindow[1]
                    }
                assertTrue("Conflict control $label must be on screen above $keyboardBounds",
                    bounds.top + origin[1] >= 0 && bounds.bottom + origin[1] <= keyboardBounds.top)
            }
            rule.runOnIdle { assertEquals("resolution result", model.conflictDraft.text); assertEquals(0, sends) }
            rule.onNode(hasSetTextAction() and hasText("resolution result")).assertIsDisplayed().performTextInput(" appended")
            rule.runOnIdle {
                assertTrue(model.conflictDraft.text.contains("resolution result"))
                assertTrue(model.conflictDraft.text.contains(" appended")); assertEquals(0, sends)
            }
            Espresso.closeSoftKeyboard()
        } finally {
            info.flags = originalFlags
            automation.serviceInfo = info
        }
    }
}
