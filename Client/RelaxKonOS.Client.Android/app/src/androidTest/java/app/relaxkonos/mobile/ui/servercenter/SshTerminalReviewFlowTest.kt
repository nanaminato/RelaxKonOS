package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import app.relaxkonos.mobile.ui.theme.TerminalType
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshTerminalReviewFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val rendered = mutableStateOf(SshTerminalUiState())
    private var live: SshTerminalUiState? = null
    private val sent = mutableListOf<String>()
    private val ended = mutableListOf<String>()
    private fun text(id: Int) = rule.activity.getString(id)
    private fun edit(value: String) {
        live = requireNotNull(live).let { it.copy(draft = value, inputRevision = it.inputRevision + 1) }
        rendered.value = requireNotNull(live)
    }
    private fun show(draft: String = "first\nsecond") {
        live = SshTerminalUiState(sessionId = "session-first", hostId = "host", connected = true, draft = draft)
        rendered.value = requireNotNull(live)
        rule.setContent { MaterialTheme { Box(Modifier.size(360.dp, 640.dp)) {
            val state = rendered.value
            SshTerminalContent("host", "user@host:22", state, {}, { sent += it }, { _, _ -> }, {},
                ::edit, {}, listOf(state), true, {}, {}, { ended += it }, readState = { live },
                imeInsets = WindowInsets(0, 0, 0, 0), terminalType = TerminalType.Native)
        } } }
    }
    private fun button(id: Int, dialog: Boolean) = rule.onNode(hasText(text(id)) and hasClickAction() and
        (if (dialog) hasAnyAncestor(isDialog()) else !hasAnyAncestor(isDialog())))
    private fun captured(id: Int): () -> Boolean = requireNotNull(button(id, true).fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    private fun send() = button(R.string.ssh_terminal_send, false).performClick()
    private fun openEnd() {
        rule.onNodeWithContentDescription(text(R.string.terminal_session_actions)).performClick()
        rule.onNode(hasText(text(R.string.terminal_close)) and hasClickAction()).performClick()
    }

    @Test fun pasteApprovalConsumesOnceAndOldConfirmOrCancelCannotAffectReopenedReview() {
        show(); send()
        val oldConfirm = captured(R.string.terminal_send); val oldCancel = captured(R.string.common_cancel)
        rule.runOnIdle { oldConfirm(); oldConfirm() }
        rule.runOnIdle { assertEquals(listOf("first\nsecond\r"), sent); edit("third\nfourth") }
        send()
        val fresh = captured(R.string.terminal_send)
        rule.runOnIdle { oldConfirm(); oldCancel(); assertEquals(1, sent.size) }
        rule.onNodeWithText(text(R.string.terminal_paste_title)).assertIsDisplayed()
        rule.runOnIdle { fresh(); fresh() }
        rule.runOnIdle { assertEquals(listOf("first\nsecond\r", "third\nfourth\r"), sent) }
    }
    @Test fun currentAuthorityRejectsChangedDraftRevisionAndOwnerBeforeComposeUpdates() {
        show(); send(); val confirm = captured(R.string.terminal_send)
        rule.runOnIdle {
            val original = requireNotNull(live)
            live = original.copy(inputRevision = original.inputRevision + 2)
            confirm(); assertTrue(sent.isEmpty())
            live = requireNotNull(live).copy(sessionId = "other")
            confirm(); assertTrue(sent.isEmpty())
            live = null; confirm(); assertTrue(sent.isEmpty())
        }
    }
    @Test fun pauseWithdrawsApprovalSynchronouslyAndResumeDoesNotReplayIt() {
        show(); send(); val confirm = captured(R.string.terminal_send)
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.activityRule.scenario.onActivity { confirm(); assertTrue(sent.isEmpty()) }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.activityRule.scenario.onActivity { confirm(); assertTrue(sent.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.onNodeWithText(text(R.string.terminal_paste_title)).assertDoesNotExist()
        send(); button(R.string.terminal_send, true).performClick()
        rule.runOnIdle { assertEquals(listOf("first\nsecond\r"), sent) }
    }
    @Test fun carriageReturnWithControlStillRequiresFullScrollablePreview() {
        val body = "echo " + "review ".repeat(400) + "\rTAIL-MUST-BE-REVIEWED"
        show(body)
        rule.onNodeWithText("Ctrl", useUnmergedTree = true).performClick()
        send()
        rule.onNodeWithText(text(R.string.terminal_paste_title)).assertIsDisplayed()
        rule.onNode(hasText(body) and hasAnyAncestor(isDialog())).assertExists()
        val scroll = rule.onNode(hasScrollAction() and hasAnyAncestor(isDialog()))
        scroll.performSemanticsAction(SemanticsActions.ScrollBy) { it(0f, 10_000f) }
        val range = scroll.fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange]
        rule.runOnIdle {
            assertTrue(range.maxValue() > 0f)
            assertEquals(range.maxValue(), range.value(), 1f)
        }
        rule.runOnIdle { assertTrue(sent.isEmpty()) }
        button(R.string.terminal_send, true).performClick()
        rule.runOnIdle { assertEquals(listOf(body), sent) }
    }
    @Test fun endConfirmationUsesOriginalSessionAndOldNodesCannotCloseNewReview() {
        show(); openEnd()
        val oldConfirm = captured(R.string.terminal_close); val oldCancel = captured(R.string.common_cancel)
        rule.runOnIdle { live = requireNotNull(live).copy(sessionId = "session-second", inputRevision = 2); oldConfirm(); assertTrue(ended.isEmpty()); rendered.value = requireNotNull(live) }
        openEnd(); val fresh = captured(R.string.terminal_close)
        rule.onNodeWithText("session-second").assertIsDisplayed()
        rule.runOnIdle { oldConfirm(); oldCancel(); assertTrue(ended.isEmpty()) }
        rule.onNodeWithText("session-second").assertIsDisplayed()
        rule.runOnIdle { fresh(); fresh() }
        rule.runOnIdle { assertEquals(listOf("session-second"), ended) }
    }
}
