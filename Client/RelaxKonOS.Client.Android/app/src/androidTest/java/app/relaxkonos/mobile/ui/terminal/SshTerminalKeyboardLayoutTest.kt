package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.ui.servercenter.SshTerminalContent
import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.SideEffect
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
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import androidx.test.espresso.Espresso.closeSoftKeyboard
import java.util.concurrent.atomic.AtomicInteger

class SshTerminalKeyboardLayoutTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val state = SshTerminalUiState(hostId = "host", connected = true, output = "user:~$ prompt-visible")
    private val sent = mutableListOf<String>()

    @Composable
    private fun TestContent(hostId: String, label: String, initial: SshTerminalUiState,
        reconnect: () -> Unit, send: (String) -> Unit, resize: (Int, Int) -> Unit, close: () -> Unit,
        modifier: Modifier = Modifier, imeInsets: WindowInsets = WindowInsets.ime) {
        var state by remember { mutableStateOf(initial.copy(sessionId = "first")) }
        SshTerminalContent(hostId, label, state, reconnect, send, resize, close,
            { state = state.copy(draft = it) }, { state = state.copy(concealInput = it) },
            { state = state.copy(fontSize = it) }, emptyList(), true, {}, {}, {}, modifier, imeInsets)
    }

    private fun show(width: Dp, height: Dp, keyboard: Dp, consumed: Dp = 0.dp, scale: Float = 1f) {
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, scale)) {
                MaterialTheme {
                    Box(Modifier.size(width, height).testTag("window")) {
                        val padding = PaddingValues(bottom = consumed)
                        TestContent("host", "user@192.168.1.5:22", state, {}, { sent += it }, { _, _ -> }, {},
                            Modifier.padding(padding).consumeWindowInsets(padding), WindowInsets(bottom = keyboard))
                    }
                }
            }
        }
    }

    private fun assertReadableEditor() {
        val editor = rule.onNode(hasSetTextAction())
        editor.assertIsDisplayed().assertHeightIsAtLeast(56.dp)
        editor.performTextInput("echo 手机输入")
        closeSoftKeyboard()
        rule.onNodeWithText("echo 手机输入").assertIsDisplayed()
        rule.onNodeWithTag("ssh-terminal-output").assertIsDisplayed()
        val window = rule.onNodeWithTag("window").fetchSemanticsNode().boundsInRoot
        assertTrue(editor.fetchSemanticsNode().boundsInRoot.bottom <= window.bottom)
    }

    @Test fun phoneInputAndPromptStayAboveKeyboard() {
        show(360.dp, 640.dp, 330.dp, consumed = 24.dp)
        assertReadableEditor()
        rule.onNodeWithText("A+").assertDoesNotExist()
        rule.onNode(hasSetTextAction()).performImeAction()
        rule.runOnIdle { assertEquals(listOf("echo 手机输入\r"), sent) }
        rule.onNode(hasSetTextAction()).assertIsDisplayed()
    }

    @Test fun tabletParentConsumedImeDoesNotApplyItTwice() {
        show(960.dp, 600.dp, 330.dp, consumed = 330.dp)
        assertReadableEditor()
    }

    @Test fun largeFontsAndShortHeightKeepTheEditorReadable() {
        show(360.dp, 500.dp, 300.dp, scale = 1.5f)
        assertReadableEditor()
        rule.onNodeWithText("Ctrl").assertDoesNotExist()
    }

    @Test fun keyboardChangesKeepDraftAndDoNotSendOrReconnect() {
        val keyboard = mutableStateOf(0.dp)
        val reconnects = mutableListOf<Unit>()
        rule.setContent {
            MaterialTheme {
                Box(Modifier.size(360.dp, 640.dp)) {
                    TestContent("host", "user@host:22", state, { reconnects += Unit }, { sent += it }, { _, _ -> }, {},
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
        rule.runOnIdle { assertTrue(sent.isEmpty()); assertTrue(reconnects.isEmpty()) }
    }

    @Test fun systemKeyboardKeepsInputVisibleAndImeSendUsesTheSameTerminal() {
        rule.runOnUiThread { rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE) }
        val keyboardBottom = AtomicInteger()
        rule.setContent {
            val bottom = WindowInsets.ime.getBottom(LocalDensity.current)
            SideEffect { keyboardBottom.set(bottom) }
            MaterialTheme {
                Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars)) {
                    TestContent("host", "user@192.168.1.5:22", state, {}, { sent += it }, { _, _ -> }, {})
                }
            }
        }
        val editor = rule.onNode(hasSetTextAction())
        editor.performClick()
        rule.waitUntil(10_000) { keyboardBottom.get() > 0 }
        editor.performTextInput("echo 手机输入")
        rule.onNodeWithText("echo 手机输入").assertIsDisplayed()
        editor.assertIsDisplayed().assertHeightIsAtLeast(56.dp)
        val window = rule.onRoot().fetchSemanticsNode().boundsInRoot
        assertTrue(editor.fetchSemanticsNode().boundsInRoot.bottom <= window.bottom - keyboardBottom.get())
        editor.performImeAction()
        rule.runOnIdle { assertEquals(listOf("echo 手机输入\r"), sent) }
        rule.onNodeWithTag("ssh-terminal-output").assertIsDisplayed()
        editor.assertIsDisplayed()
    }
}
