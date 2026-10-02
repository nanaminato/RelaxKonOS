package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class TerminalLoadingTest {
    @get:Rule val rule = createComposeRule()

    @Test fun entryPaintsLoadingBeforeCreatingTheRenderer() {
        var rendered = 0
        rule.mainClock.autoAdvance = false
        rule.setContent {
            MaterialTheme {
                TerminalEntry("host", Modifier.fillMaxSize()) {
                    SideEffect { rendered++ }
                    Text("Renderer ready")
                }
            }
        }
        rule.onNodeWithTag("terminal-loading").assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, rendered) }
        rule.mainClock.advanceTimeBy(100)
        rule.onNodeWithText("Renderer ready").assertIsDisplayed()
        rule.onNodeWithTag("terminal-loading").assertDoesNotExist()
    }

    @Test fun leavingDuringEntryCancelsThePendingRenderer() {
        var rendered = 0
        rule.mainClock.autoAdvance = false
        rule.setContent {
            MaterialTheme {
                var terminal by remember { mutableStateOf(true) }
                Column {
                    TextButton(onClick = { terminal = false }) { Text("Files") }
                    if (terminal) TerminalEntry("host", Modifier.weight(1f)) {
                        SideEffect { rendered++ }
                        Text("Renderer ready")
                    } else Text("File browser")
                }
            }
        }
        rule.onNodeWithText("Files").performClick()
        rule.mainClock.advanceTimeBy(100)
        rule.onNodeWithText("File browser").assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, rendered) }
    }

    @Test fun connectingSpinnerEndsOnFailureInsteadOfCoveringRetryActions() {
        val owner = SessionState.Active("server", "https://host:5090", "nana", "workspace", emptySet(), "linux", ExecutionEligibility.Available,
            workspaceId = "11111111-1111-1111-1111-111111111111")
        val state = mutableStateOf(ServerTerminalState(connecting = true))
        rule.setContent {
            MaterialTheme {
                ServerTerminalContent(owner, state.value, {}, {}, { true }, { _, _ -> }, {}, {}, Modifier.fillMaxSize())
            }
        }
        rule.onNodeWithTag("terminal-loading").assertIsDisplayed()
        rule.runOnIdle { state.value = state.value.copy(connecting = false, error = true) }
        rule.onNodeWithTag("terminal-loading").assertDoesNotExist()
    }
}
