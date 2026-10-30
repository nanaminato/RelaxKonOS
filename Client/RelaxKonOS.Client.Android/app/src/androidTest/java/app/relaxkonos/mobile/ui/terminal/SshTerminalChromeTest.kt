package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import app.relaxkonos.mobile.ui.servercenter.SshTerminalContent
import app.relaxkonos.mobile.ui.theme.TerminalType
import org.junit.Rule
import org.junit.Test

/** The phone chrome is state outside the composable in production; these checks drive it the same way. */
class SshTerminalChromeTest {
    @get:Rule val rule = createComposeRule()
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val state = SshTerminalUiState(sessionId = "s1", hostId = "host", connected = true,
        output = "prompt", transcript = "prompt", draft = "draft")

    private fun show(chrome: TerminalChrome, collapsed: Boolean) {
        rule.setContent {
            var currentChrome by remember { mutableStateOf(chrome) }
            var currentCollapsed by remember { mutableStateOf(collapsed) }
            MaterialTheme {
                Box(Modifier.size(412.dp, 907.dp)) {
                    SshTerminalContent("host", "user@host:22", state,
                        onConnect = {}, onSend = {}, onResize = { _, _ -> }, onClose = {},
                        onDraftChange = {}, onFontSizeChange = {},
                        sessions = listOf(state), canCreate = true,
                        onSelect = {}, onNew = {}, onEnd = {}, readState = { state },
                        imeInsets = WindowInsets(0, 0, 0, 0), terminalType = TerminalType.Native,
                        chrome = currentChrome, onChromeChange = { currentChrome = it },
                        inputCollapsed = currentCollapsed,
                        onInputCollapsedChange = { currentCollapsed = it })
                }
            }
        }
    }

    @Test fun collapsedEditorKeepsTheTranscriptAndRestoresFromItsBar() {
        show(TerminalChrome.Compact, collapsed = false)
        rule.onNode(hasSetTextAction()).assertIsDisplayed()
        rule.onNodeWithContentDescription(context.getString(R.string.terminal_session_actions)).performClick()
        rule.onNodeWithText(context.getString(R.string.terminal_input_hide)).performClick()
        rule.onNode(hasSetTextAction()).assertDoesNotExist()
        rule.onNodeWithTag("terminal-input-collapsed").performClick()
        rule.onNode(hasSetTextAction()).assertIsDisplayed()
    }

    @Test fun expandShowsFontControlsAndCollapseReturnsToTheOneLineBar() {
        show(TerminalChrome.Compact, collapsed = false)
        rule.onNodeWithTag("terminal-topbar").assertIsDisplayed()
        rule.onNodeWithContentDescription(context.getString(R.string.terminal_session_actions)).performClick()
        rule.onNodeWithText(context.getString(R.string.terminal_chrome_expand)).performClick()
        rule.onNodeWithText("A+").assertIsDisplayed()
        rule.onNodeWithContentDescription(context.getString(R.string.terminal_chrome_collapse)).performClick()
        rule.onNodeWithTag("terminal-topbar").assertIsDisplayed()
    }
}
