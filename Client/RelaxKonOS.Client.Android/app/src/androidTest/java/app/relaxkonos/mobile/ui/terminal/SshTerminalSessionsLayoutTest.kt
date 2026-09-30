package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerCenterSshTerminal
import app.relaxkonos.mobile.servercenter.SshTerminalConnection
import app.relaxkonos.mobile.servercenter.SshTerminalConnector
import app.relaxkonos.mobile.servercenter.SshTerminalSessions
import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import app.relaxkonos.mobile.ui.servercenter.SshTerminalContent
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshTerminalSessionsLayoutTest {
    @get:Rule val rule = createComposeRule()
    private class Shell : ServerCenterSshTerminal {
        val output = Channel<String>(Channel.UNLIMITED)
        val writes = mutableListOf<String>()
        val closes = AtomicInteger()
        override suspend fun read() = output.receiveCatching().getOrNull()
        override suspend fun write(value: String) { writes += value }
        override suspend fun resize(columns: Int, rows: Int) {}
        override fun close() { closes.incrementAndGet(); output.close() }
    }

    @Test fun leavingForDeploymentRetainsShellAndMultiSessionControlsKeepDraftsSeparate() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        val shells = mutableListOf<Shell>()
        val manager = SshTerminalSessions(scope, SshTerminalConnector {
            val shell = Shell().also { shells += it }
            SshTerminalConnection(shell, AutoCloseable {})
        }, { true })
        val visible = mutableStateOf(true)
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        try {
            rule.setContent {
                MaterialTheme {
                    Box(Modifier.size(360.dp, 640.dp)) {
                        if (!visible.value) Text("Deployment page") else {
                            val all by manager.state.collectAsState()
                            LaunchedEffect(Unit) { manager.enter("host") }
                            val state = all.selectedForHost("host") ?: SshTerminalUiState(hostId = "host")
                            SshTerminalContent("host", "user@192.168.1.5:22", state,
                                { manager.reconnect(state.sessionId) }, { manager.send(state.sessionId, it) },
                                { c, r -> manager.resize(state.sessionId, c, r) }, {},
                                { manager.updateDraft(state.sessionId, it) }, { manager.concealInput(state.sessionId, it) },
                                { manager.fontSize(state.sessionId, it) }, all.sessions, true, manager::select,
                                { manager.create("host") }, manager::end, imeInsets = WindowInsets(0, 0, 0, 0))
                        }
                    }
                }
            }
            rule.onNode(hasSetTextAction()).performTextInput("first draft")
            lateinit var first: String
            rule.runOnIdle {
                first = manager.state.value.sessions.single().sessionId
                visible.value = false
            }
            rule.onNodeWithText("Deployment page").assertIsDisplayed()
            rule.runOnIdle {
                assertEquals(0, shells.single().closes.get())
                shells.single().output.trySend("Output while in deployment")
                visible.value = true
            }
            rule.onNodeWithText("first draft").assertIsDisplayed()
            rule.onNodeWithText("Output while in deployment").assertIsDisplayed()
            rule.runOnIdle { assertEquals(1, shells.size) }
            rule.onNodeWithText(context.getString(R.string.terminal_new)).performClick()
            rule.onNode(hasSetTextAction()).performTextInput("second command")
            rule.onNode(hasSetTextAction()).performImeAction()
            rule.runOnIdle {
                assertEquals(2, shells.size)
                assertTrue(shells.first().writes.isEmpty())
                assertEquals(listOf("second command\r"), shells.last().writes)
            }
            rule.onNodeWithText(first.take(8), substring = true).performClick()
            rule.onNodeWithText("first draft").assertIsDisplayed()
            rule.onNodeWithContentDescription(context.getString(R.string.terminal_session_actions)).performClick()
            val end = context.getString(R.string.terminal_close)
            rule.onNode(hasText(end) and hasClickAction()).performClick()
            rule.onNode(hasText(end) and hasClickAction()).performClick()
            rule.waitUntil(5_000) { shells.first().closes.get() == 1 }
            rule.runOnIdle {
                assertEquals(1, manager.state.value.sessions.size)
                assertTrue(manager.state.value.sessions.single().connected)
                assertEquals(0, shells.last().closes.get())
            }
        } finally {
            rule.runOnIdle { manager.state.value.sessions.forEach { manager.end(it.sessionId) }; scope.cancel() }
        }
    }
}
