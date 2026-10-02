package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Surface
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toPixelMap
import app.relaxkonos.mobile.ui.theme.TerminalType
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class TerminalLoadingTest {
    @get:Rule val rule = createComposeRule()

    @Test fun xtermDrawingStaysInsideOutputWhileChromeRemainsPainted() = assertXtermChrome(1000, 600, true)

    @Test fun phoneChromeRemainsPaintedDuringXtermEntry() = assertXtermChrome(360, 700, false)

    private fun assertXtermChrome(width: Int, height: Int, sidebar: Boolean) {
        val owner = SessionState.Active("server", "https://host:5090", "nana", "workspace", emptySet(), "linux", ExecutionEligibility.Available,
            workspaceId = "11111111-1111-1111-1111-111111111111")
        rule.setContent {
            CompositionLocalProvider(LocalDensity provides Density(1f)) {
                MaterialTheme {
                    Surface(Modifier.requiredSize(width.dp, height.dp).testTag("terminal-test-page"), color = Color.White) {
                        ServerTerminalContent(owner, ServerTerminalState(connected = true, sessionId = "first", rawOutput = "prompt"),
                            {}, {}, { true }, { _, _ -> }, {}, {}, imeInsets = WindowInsets(0), terminalType = TerminalType.Xterm)
                    }
                }
            }
        }
        fun assertChromePainted() {
            val page = rule.onNodeWithTag("terminal-test-page")
            val origin = page.fetchSemanticsNode().boundsInRoot.topLeft
            val output = rule.onNodeWithTag("terminal-output-region").fetchSemanticsNode().boundsInRoot
            val pixels = page.captureToImage().toPixelMap()
            // Blank gutter just above the output belongs to Compose chrome, never Chromium.
            val pixel = pixels[(output.center.x - origin.x).toInt(), (output.top - origin.y - 2).toInt()]
            assertTrue("terminal must not paint over chrome: $pixel", pixel.red > 0.9f && pixel.green > 0.9f && pixel.blue > 0.9f)
            if (sidebar) rule.onNodeWithTag("terminal-session-sidebar").assertIsDisplayed()
            rule.onNodeWithText("nana · https://host:5090").assertIsDisplayed()
            rule.onNode(hasSetTextAction()).assertIsDisplayed()
        }
        assertChromePainted()
        rule.waitUntil(10_000) { rule.onAllNodesWithTag("terminal-loading").fetchSemanticsNodes().isEmpty() }
        assertChromePainted()
    }

    @Test fun tabletChromeAppearsWhileOnlyOutputIsLoading() {
        val owner = SessionState.Active("server", "https://host:5090", "nana", "workspace", emptySet(), "linux", ExecutionEligibility.Available,
            workspaceId = "11111111-1111-1111-1111-111111111111")
        rule.mainClock.autoAdvance = false
        rule.setContent {
            CompositionLocalProvider(LocalDensity provides Density(1f)) {
                MaterialTheme {
                    ServerTerminalContent(owner, ServerTerminalState(connected = true, sessionId = "first"), {}, {}, { true }, { _, _ -> }, {}, {},
                        Modifier.requiredSize(1000.dp, 600.dp), imeInsets = WindowInsets(0))
                }
            }
        }
        rule.onNodeWithTag("terminal-session-sidebar").assertIsDisplayed()
        rule.onNodeWithText("Ctrl").assertIsDisplayed()
        rule.onNode(hasSetTextAction()).assertIsDisplayed()
        val loading = rule.onNodeWithTag("terminal-loading").assertIsDisplayed().getUnclippedBoundsInRoot()
        val output = rule.onNodeWithTag("terminal-output-region").getUnclippedBoundsInRoot()
        val sidebar = rule.onNodeWithTag("terminal-session-sidebar").getUnclippedBoundsInRoot()
        val editor = rule.onNode(hasSetTextAction()).getUnclippedBoundsInRoot()
        assertEquals(output, loading)
        assertTrue(loading.left >= sidebar.right)
        assertTrue(loading.bottom <= editor.top)
        rule.onNodeWithTag("terminal-output").assertDoesNotExist()
        rule.mainClock.advanceTimeBy(100)
        rule.onNodeWithTag("terminal-loading").assertDoesNotExist()
        rule.onNodeWithTag("terminal-session-sidebar").assertIsDisplayed()
    }

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
