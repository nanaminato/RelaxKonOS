package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertHeightIsAtLeast
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.core.net.TerminalSessionSummary
import org.junit.Assert.assertTrue
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.espresso.Espresso.closeSoftKeyboard
import android.graphics.Bitmap
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

/** Exercises the actual terminal UI under deterministic keyboard and shell inset budgets. */
class TerminalKeyboardLayoutTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val owner = SessionState.Active("server", "https://host:5090", "nana", "studio", emptySet(), "linux", ExecutionEligibility.Available, workspaceId = "11111111-1111-1111-1111-111111111111")
    private val state = ServerTerminalState(
        connected = true, sessionId = "first", output = "nana@server:~$ ps\nprompt-visible",
        sessions = listOf("first", "second").map { id -> TerminalSessionSummary().also { it.sessionId = id } },
    )
    private fun secondSessionLabel() = InstrumentationRegistry.getInstrumentation().targetContext
        .getString(R.string.terminal_session_name, 2)

    private fun show(width: Dp, height: Dp, keyboard: Dp, consumedBottom: Dp = 0.dp, scale: Float = 1f) {
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, scale)) {
                MaterialTheme {
                    Box(Modifier.size(width, height).testTag("window")) {
                        val shellPadding = PaddingValues(bottom = consumedBottom)
                        ServerTerminalContent(owner, state, {}, {}, { true }, { _, _ -> }, {}, {},
                            modifier = Modifier.padding(shellPadding).consumeWindowInsets(shellPadding),
                            imeInsets = WindowInsets(bottom = keyboard))
                    }
                }
            }
        }
    }

    private fun assertReadableEditorAndOutput() {
        val editor = rule.onNode(hasSetTextAction())
        editor.assertIsDisplayed().assertHeightIsAtLeast(56.dp)
        editor.performTextInput("echo 手机输入")
        closeSoftKeyboard()
        rule.onNodeWithText("echo 手机输入").assertIsDisplayed()
        rule.onNodeWithText(state.output).assertIsDisplayed()
        val bounds = editor.fetchSemanticsNode().boundsInRoot
        val window = rule.onNodeWithTag("window").fetchSemanticsNode().boundsInRoot
        assertTrue("editor stays inside the visible window", bounds.bottom <= window.bottom)
    }

    @Test fun phoneEditorStaysAboveKeyboardWithNavigationInsetConsumed() {
        show(360.dp, 640.dp, 330.dp, consumedBottom = 24.dp)
        assertReadableEditorAndOutput()
        rule.onNodeWithText("A+").assertDoesNotExist()
    }

    @Test fun tabletLandscapeKeepsEditorHeightAndTerminalOutput() {
        show(960.dp, 600.dp, 330.dp)
        assertReadableEditorAndOutput()
    }

    @Test fun tallViewportStillFoldsManagementControlsWhileTyping() {
        show(360.dp, 900.dp, 330.dp)
        assertReadableEditorAndOutput()
        rule.onNodeWithText("A+").assertDoesNotExist()
        rule.onNodeWithText(secondSessionLabel(), substring = true).assertDoesNotExist()
    }

    @Test fun parentThatAlreadyAvoidedKeyboardDoesNotApplyImeInsetTwice() {
        show(960.dp, 600.dp, 330.dp, consumedBottom = 330.dp)
        assertReadableEditorAndOutput()
    }

    @Test fun largeSystemFontAndShortViewportStillKeepTextReadable() {
        show(360.dp, 500.dp, 300.dp, scale = 1.5f)
        assertReadableEditorAndOutput()
        rule.onNodeWithText("Ctrl").assertDoesNotExist()
    }

    @Test fun keyboardClosedRestoresSessionAndFontControls() {
        show(360.dp, 640.dp, 0.dp)
        assertReadableEditorAndOutput()
        rule.onNodeWithText("A+").assertIsDisplayed()
        rule.onNodeWithText(secondSessionLabel(), substring = true).assertIsDisplayed()
    }

    @Test fun keyboardShowAndHidePreserveTheUnsentDraft() {
        val keyboard = mutableStateOf(0.dp)
        rule.setContent {
            MaterialTheme {
                Box(Modifier.size(360.dp, 640.dp)) {
                    ServerTerminalContent(owner, state, {}, {}, { true }, { _, _ -> }, {}, {},
                        imeInsets = WindowInsets(bottom = keyboard.value))
                }
            }
        }
        rule.onNode(hasSetTextAction()).performTextInput("unsent draft")
        rule.runOnIdle { keyboard.value = 330.dp }
        rule.onNodeWithText("unsent draft").assertIsDisplayed()
        rule.onNode(hasSetTextAction()).assertHeightIsAtLeast(56.dp)
        rule.runOnIdle { keyboard.value = 0.dp }
        closeSoftKeyboard()
        rule.onNodeWithText("unsent draft").assertIsDisplayed()
        rule.onNodeWithText("A+").assertIsDisplayed()
    }

    @Test fun actualSystemKeyboardKeepsDraftAndCursorAboveIme() {
        rule.runOnUiThread { rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE) }
        val keyboardBottom = AtomicInteger()
        rule.setContent {
            val imeBottom = WindowInsets.ime.getBottom(LocalDensity.current)
            SideEffect { keyboardBottom.set(imeBottom) }
            MaterialTheme {
                Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars)) {
                    ServerTerminalContent(owner, state, {}, {}, { true }, { _, _ -> }, {}, {})
                }
            }
        }
        val editor = rule.onNode(hasSetTextAction())
        editor.performClick()
        rule.waitUntil(10_000) { keyboardBottom.get() > 0 }
        editor.performTextInput("echo 手机输入")
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val evidence = File(instrumentation.targetContext.getExternalFilesDir(null), "terminal-real-keyboard.png")
        evidence.outputStream().use { stream ->
            instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, stream)
        }
        editor.assertIsDisplayed().assertHeightIsAtLeast(56.dp)
        rule.onNodeWithText("echo 手机输入").assertIsDisplayed()
        val root = rule.onRoot().fetchSemanticsNode().boundsInRoot
        assertTrue("editor must stay above the real IME", editor.fetchSemanticsNode().boundsInRoot.bottom <= root.bottom - keyboardBottom.get())
        rule.onNodeWithText(state.output).assertIsDisplayed()
    }

    @Test fun wideViewportUsesSessionSidebarAndLeavesEditorInTheTerminalColumn() {
        show(960.dp, 600.dp, 0.dp)
        val sidebar = rule.onNodeWithTag("terminal-session-sidebar").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        val output = rule.onNodeWithTag("terminal-output").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        val editor = rule.onNode(hasSetTextAction()).assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        assertTrue(output.left >= sidebar.right)
        assertTrue(editor.left >= sidebar.right)
    }

    @Test fun fontAndKeyboardConstraintsCollapseSidebarWithoutLosingDraft() {
        val scale = mutableStateOf(1f)
        val keyboard = mutableStateOf(0.dp)
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, scale.value)) {
                MaterialTheme {
                    Box(Modifier.size(960.dp, 600.dp)) {
                        ServerTerminalContent(owner, state, {}, {}, { true }, { _, _ -> }, {}, {}, imeInsets = WindowInsets(bottom = keyboard.value))
                    }
                }
            }
        }
        rule.onNode(hasSetTextAction()).performTextInput("keep draft")
        rule.onNodeWithTag("terminal-session-sidebar").assertIsDisplayed()
        rule.runOnIdle { scale.value = 1.5f; keyboard.value = 330.dp }
        rule.onNodeWithTag("terminal-session-sidebar").assertDoesNotExist()
        rule.onNodeWithText("keep draft").assertIsDisplayed()
        rule.onNode(hasSetTextAction()).assertHeightIsAtLeast(56.dp)
    }

    @Test fun sessionSwitchRestoresDraftAndCancelsAReviewedPasteToThePreviousSession() {
        val selected = mutableStateOf("first")
        val presentation = TerminalPresentation().apply { bindOwner(owner) }
        rule.setContent {
            MaterialTheme {
                Box(Modifier.size(960.dp, 600.dp)) {
                    ServerTerminalContent(owner, state.copy(sessionId = selected.value), {}, { selected.value = it ?: "new" }, { true }, { _, _ -> }, {}, {},
                        imeInsets = WindowInsets(0), presentation = presentation)
                }
            }
        }
        rule.onNode(hasSetTextAction()).performTextInput("first draft")
        rule.runOnIdle { presentation.preparePaste("echo first\necho second") }
        rule.runOnIdle { selected.value = "second" }
        rule.waitForIdle()
        rule.runOnIdle { assertEquals(null, presentation.pasteReview) }
        rule.onNode(hasSetTextAction()).performTextInput("second draft")
        rule.runOnIdle { selected.value = "first" }
        rule.onNodeWithText("first draft").assertIsDisplayed()
    }

    @Test fun ansiAndWideOutputUsesRenderedTextAndSearchWithoutSendingInput() {
        val transcript = TerminalTranscript()
        val plain = transcript.append("\u001b[31m红色\u001b[0m 日本語 😀\u001b[?25l".toByteArray())
        val presentation = TerminalPresentation().apply { bindOwner(owner) }
        val writes = AtomicInteger()
        rule.setContent {
            MaterialTheme {
                Box(Modifier.size(960.dp, 600.dp)) {
                    ServerTerminalContent(owner, state.copy(output = plain, frame = transcript.frame), {}, {}, { writes.incrementAndGet(); true }, { _, _ -> }, {}, {},
                        imeInsets = WindowInsets(0), presentation = presentation)
                }
            }
        }
        rule.onNodeWithTag("terminal-output").assertIsDisplayed()
        rule.runOnIdle { presentation.searchOpen = true; presentation.search = "日本語" }
        rule.onNodeWithText(transcript.frame.text).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes.get()) }
    }
}
