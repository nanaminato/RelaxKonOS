package app.relaxkonos.mobile.ui.editor

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
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.unit.dp
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.filters.SdkSuppress
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TextEditorRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import java.io.File
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class TextEditorFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private val file = RemoteTextFile("/review.txt", "original", "a".repeat(64), "utf-8", false, "none")
    private var readFile = file
    private val receipt = CompletableDeferred<ApiResult<RemoteTextFile>>()
    private var writes = 0
    private var savedCallbacks = 0
    private lateinit var editor: TextEditorViewModel
    private val closed = mutableStateOf(false)
    private val darkTheme = mutableStateOf(false)

    private fun showEditor(existingFile: Boolean = true) {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", emptySet()),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun textFile(serverUrl: String, accessToken: String, path: String) = ApiResult.Success(readFile)
            override suspend fun saveTextFile(serverUrl: String, accessToken: String, file: RemoteTextFile, content: String): ApiResult<RemoteTextFile> {
                writes++
                return receipt.await()
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://editor-review.invalid"), "review", "test".toCharArray()) {} }
        rule.runOnUiThread {
            rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            editor = TextEditorViewModel(session, TextEditorRepository(gateway, session))
            store.put("editor", editor)
            editor.start(session.state.value as SessionState.Active, file.path.takeIf { existingFile }, null)
        }
        rule.setContent { MaterialTheme(colorScheme = if (darkTheme.value) darkColorScheme() else lightColorScheme()) {
            if (closed.value) Text("Parent page")
            else TextEditorContent(editor, file.path.takeIf { existingFile }, null, { savedCallbacks++ }, { closed.value = true })
        } }
        rule.waitUntil(5_000) { !editor.busy && (!existingFile || editor.baseline != null) }
    }

    @After fun cleanup() { rule.runOnUiThread { store.clear() } }

    @Test fun unsavedCloseCanReturnToDraftOrExplicitlyDiscard() {
        showEditor()
        rule.onNode(hasSetTextAction() and hasText("original")).performTextReplacement("retained draft")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_unsaved)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.editor_continue_editing)).performClick()
        rule.onNodeWithText("retained draft").assertIsDisplayed()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes); assertEquals(0, savedCallbacks); assertEquals("", editor.value.text) }
    }

    @Test fun busySaveBlocksBackAndUnknownReceiptKeepsDraftUntilReadback() {
        showEditor()
        rule.onNode(hasSetTextAction() and hasText("original")).performTextReplacement("unconfirmed draft")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.git_save)).performClick()
        rule.waitUntil(5_000) { editor.busy }
        rule.onNodeWithText(text(R.string.git_save)).assertIsNotEnabled()
        rule.onNodeWithText("unconfirmed draft").assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed.value); assertEquals(1, writes); assertEquals(0, savedCallbacks) }
        receipt.complete(ApiResult.Transport(null))
        rule.waitUntil(5_000) { editor.unknown && !editor.busy }
        rule.onNodeWithText(text(R.string.editor_unknown)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText("unconfirmed draft").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.git_save)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { editor.latest != null && !editor.busy }
        rule.runOnIdle {
            assertEquals("unconfirmed draft", editor.value.text)
            assertEquals("original", editor.latest?.content)
            assertTrue(editor.unknown); assertEquals(1, writes); assertEquals(0, savedCallbacks)
        }
    }

    @Test fun saveConflictShowsComparisonAndRetainsDraftWithoutGenericFailureModal() {
        showEditor()
        rule.onNode(hasSetTextAction() and hasText("original")).performTextReplacement("conflicting draft")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.git_save)).performClick()
        rule.waitUntil(5_000) { editor.busy }
        rule.runOnIdle { readFile = file.copy(content = "server revision", version = "b".repeat(64)) }
        receipt.complete(ApiResult.Problem(409, "text-file-changed", null))
        rule.waitUntil(5_000) { editor.latest != null && !editor.busy }
        rule.onNodeWithText(text(R.string.editor_failed)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.editor_conflict)).assertIsDisplayed()
        rule.onNodeWithText("conflicting draft").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.git_save)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals("server revision", editor.latest?.content); assertEquals(1, writes); assertEquals(0, savedCallbacks) }
        rule.onNodeWithText(text(R.string.git_compare_latest)).performScrollTo().performClick()
        rule.onNodeWithText("conflicting draft").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.git_save)).assertIsEnabled()
        rule.runOnIdle {
            assertNull(editor.latest); assertEquals("server revision", editor.baseline?.content)
            assertEquals("conflicting draft", editor.value.text); assertEquals(1, writes); assertEquals(0, savedCallbacks)
        }
    }

    @Test fun searchCloseRemainsAboveActualKeyboardAndReceivesSystemTouch() =
        keyboardActionContract(R.string.editor_search_replace, R.string.editor_find, R.string.common_close)

    @Test fun saveAsCancelRemainsAboveActualKeyboardAndReceivesSystemTouch() =
        keyboardActionContract(R.string.editor_save_as, R.string.editor_destination, R.string.common_cancel)

    @Test fun newDestinationKeepsHeaderFieldAndSaveVisibleWithActualKeyboard() {
        showEditor(existingFile = false)
        rule.onAllNodes(hasSetTextAction()).onFirst().performTextInput("new draft")
        Espresso.closeSoftKeyboard()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val originalFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            rule.onNodeWithText(text(R.string.editor_destination)).performClick()
            rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
            rule.waitForIdle()
            val evidence = File(InstrumentationRegistry.getInstrumentation().targetContext.getExternalFilesDir(null), "editor-new-destination-keyboard.png")
            evidence.outputStream().use { automation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it) }
            rule.onNodeWithText(text(R.string.editor_title)).assertIsDisplayed()
            rule.onNodeWithText(text(R.string.editor_destination)).assertIsDisplayed().performTextInput("/tmp/review-new.txt")
            rule.onNodeWithText(text(R.string.git_save)).assertIsDisplayed().assertIsEnabled()
            rule.onNodeWithText(text(R.string.editor_destination)).assertHeightIsAtLeast(56.dp)
            val keyboard = automation.windows.single { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
            val keyboardBounds = Rect().also(keyboard::getBoundsInScreen)
            listOf(R.string.editor_title, R.string.editor_destination, R.string.git_save).forEach { label ->
                val bounds = screenBounds(rule.onNodeWithText(text(label)))
                assertFalse("Editor control $label must have screen bounds", bounds.isEmpty)
                assertTrue("Editor control $label at $bounds must stay on screen above $keyboardBounds",
                    bounds.top >= 0 && bounds.bottom <= keyboardBounds.top)
            }
            rule.runOnIdle {
                assertEquals("/tmp/review-new.txt", editor.destination)
                assertEquals("new draft", editor.value.text)
                assertEquals(0, writes); assertEquals(0, savedCallbacks)
            }
            Espresso.closeSoftKeyboard()
            rule.onNodeWithText("new draft").assertIsDisplayed()
            rule.onNodeWithText("/tmp/review-new.txt").assertIsDisplayed()
        } finally {
            info.flags = originalFlags
            automation.serviceInfo = info
        }
    }

    @Test
    @SdkSuppress(minSdkVersion = 30)
    fun systemBarIconsFollowEditorSurfaceWithoutChangingDraft() {
        showEditor(existingFile = false)
        rule.onAllNodes(hasSetTextAction()).onFirst().performTextInput("theme draft")
        Espresso.closeSoftKeyboard()
        fun assertIcons(light: Boolean) {
            Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
                .inRoot(androidx.test.espresso.matcher.RootMatchers.isDialog()).check { view, failure ->
                    if (failure != null) throw failure
                    val appearance = requireNotNull(view.windowInsetsController).systemBarsAppearance
                    assertEquals(light, appearance and android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS != 0)
                    assertEquals(light, appearance and android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS != 0)
                }
        }
        assertIcons(true)
        rule.runOnIdle { darkTheme.value = true }
        rule.onNodeWithText("theme draft").assertIsDisplayed()
        assertIcons(false)
        rule.runOnIdle { darkTheme.value = false }
        rule.onNodeWithText("theme draft").assertIsDisplayed()
        assertIcons(true)
        rule.runOnIdle { assertEquals("theme draft", editor.value.text); assertEquals(0, writes); assertEquals(0, savedCallbacks) }
    }

    private fun keyboardActionContract(tool: Int, field: Int, actionLabel: Int) {
        showEditor()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val originalFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            rule.onNode(hasText(text(tool)) and hasClickAction()).performClick()
            rule.onNodeWithText(text(field)).performClick()
            rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
            rule.waitForIdle()
            val evidence = File(InstrumentationRegistry.getInstrumentation().targetContext.getExternalFilesDir(null), "editor-$tool-keyboard.png")
            evidence.outputStream().use { automation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it) }
            val keyboard = automation.windows.single { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
            val keyboardBounds = Rect().also(keyboard::getBoundsInScreen)
            val closeBounds = screenBounds(rule.onNode(hasText(text(actionLabel)) and hasClickAction()))
            assertFalse(closeBounds.isEmpty)
            assertTrue("Close $closeBounds must remain above the real keyboard $keyboardBounds", closeBounds.bottom <= keyboardBounds.top)
            val down = SystemClock.uptimeMillis()
            listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP).forEach { action ->
                val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action,
                    closeBounds.exactCenterX(), closeBounds.exactCenterY(), 0)
                try {
                    event.source = InputDevice.SOURCE_TOUCHSCREEN
                    assertTrue(automation.injectInputEvent(event, true))
                } finally { event.recycle() }
            }
            rule.onNodeWithText(text(field)).assertDoesNotExist()
            rule.onNodeWithText("original").assertIsDisplayed()
            rule.runOnIdle { assertEquals(0, writes); assertEquals(0, savedCallbacks) }
        } finally {
            info.flags = originalFlags
            automation.serviceInfo = info
        }
    }

    private fun screenBounds(node: SemanticsNodeInteraction): Rect {
        val bounds = node.fetchSemanticsNode().boundsInWindow
        val origin = IntArray(2)
        Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
            .inRoot(object : org.hamcrest.TypeSafeMatcher<androidx.test.espresso.Root>() {
                override fun describeTo(description: org.hamcrest.Description) {
                    description.appendText("focused editor dialog")
                }
                override fun matchesSafely(root: androidx.test.espresso.Root) =
                    androidx.test.espresso.matcher.RootMatchers.isDialog().matches(root) && root.decorView.hasWindowFocus()
            })
            .check { view, failure ->
                if (failure != null) throw failure
                val inWindow = IntArray(2)
                view.getLocationOnScreen(origin)
                view.getLocationInWindow(inWindow)
                origin[0] -= inWindow[0]
                origin[1] -= inWindow[1]
            }
        return Rect(bounds.left.toInt() + origin[0], bounds.top.toInt() + origin[1],
            bounds.right.toInt() + origin[0], bounds.bottom.toInt() + origin[1])
    }
}
