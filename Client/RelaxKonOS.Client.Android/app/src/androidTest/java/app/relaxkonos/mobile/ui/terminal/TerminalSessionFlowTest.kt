package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.espresso.Espresso.pressBack
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.core.net.TerminalSessionSummary
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class TerminalSessionFlowTest {
    @get:Rule val rule = createComposeRule()
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private fun label(id: Int) = context.getString(id)
    private val owner = SessionState.Active("server", "https://host", "review", "work", emptySet(), "linux",
        ExecutionEligibility.Available, workspaceId = "11111111-1111-1111-1111-111111111111")
    private val initial = ServerTerminalState(connected = true, sessionId = "first",
        sessions = listOf("first", "second").map { id -> TerminalSessionSummary().also { it.sessionId = id } })

    @Test fun rejectedSendPreservesDraftAndDisconnectPreventsSubmission() {
        val state = mutableStateOf(initial)
        var attempts = 0
        var accept = false
        rule.setContent {
            MaterialTheme { Box(Modifier.size(960.dp, 600.dp)) {
                ServerTerminalContent(owner, state.value, {}, {}, { attempts++; accept }, { _, _ -> }, {}, {},
                    imeInsets = WindowInsets(0))
            } }
        }
        rule.onNode(hasSetTextAction()).performTextInput("unsent command")
        rule.onNodeWithText(label(R.string.terminal_send)).performClick()
        rule.onNodeWithText("unsent command").assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, attempts); state.value = initial.copy(connected = false) }
        rule.onNodeWithText(label(R.string.terminal_send)).assertIsNotEnabled()
        rule.onNodeWithText("unsent command").assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, attempts); accept = true; state.value = initial }
        rule.onNodeWithText(label(R.string.terminal_send)).performClick()
        rule.runOnIdle { assertEquals(2, attempts) }
        rule.onNodeWithText("unsent command").assertDoesNotExist()
    }

    @Test fun closeOthersKeepsReviewedTargetsAcrossSelectionChangeAndBackCancels() {
        val state = mutableStateOf(initial)
        val closed = mutableListOf<String>()
        rule.setContent {
            MaterialTheme { Box(Modifier.size(960.dp, 600.dp)) {
                ServerTerminalContent(owner, state.value, {}, {}, { true }, { _, _ -> },
                    { closed.add(it) }, { closed.addAll(it) }, imeInsets = WindowInsets(0))
            } }
        }
        fun openReview() {
            rule.onNodeWithContentDescription(label(R.string.terminal_session_actions)).performClick()
            rule.onNodeWithText(label(R.string.terminal_close_others)).performClick()
        }
        openReview()
        pressBack()
        rule.runOnIdle { assertTrue(closed.isEmpty()) }
        openReview()
        rule.runOnIdle { state.value = initial.copy(sessionId = "second", busy = true) }
        rule.onNode(hasText(label(R.string.terminal_close)) and hasClickAction()).assertIsNotEnabled()
        rule.runOnIdle { assertTrue(closed.isEmpty()); state.value = initial.copy(sessionId = "second") }
        rule.onNode(hasText(label(R.string.terminal_close)) and hasClickAction()).performClick()
        rule.runOnIdle { assertEquals(listOf("second"), closed) }
    }
}
