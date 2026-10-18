package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.test.espresso.Espresso
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshLocalForwardRequest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshForwardEditorLifecycleTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val visible = mutableStateOf(true)
    private val requests = mutableListOf<SshLocalForwardRequest>()
    private var closes = 0
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() = rule.setContent { MaterialTheme {
        if (visible.value) SshForwardEditor(SshLocalForwardRequest(8080), false, { true }, { closes++ }, { requests += it })
    } }
    private fun action(id: Int) = requireNotNull(rule.onNodeWithText(text(id))
        .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    @Test fun disposedEditorRejectsOriginalSubmitAndClose() {
        show(); val start = action(R.string.ssh_forward_start); val close = action(R.string.common_cancel)
        rule.runOnIdle { visible.value = false }
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertDoesNotExist()
        rule.runOnIdle { start(); close(); assertTrue(requests.isEmpty()); assertEquals(0, closes) }
    }
    @Test fun pausedEditorRejectsCallbacksWithoutWaitingForCompose() {
        show(); val start = action(R.string.ssh_forward_start); val close = action(R.string.common_cancel)
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { start(); close(); assertTrue(requests.isEmpty()); assertEquals(0, closes) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNodeWithText(text(R.string.ssh_forward_start)).performClick()
        rule.runOnIdle { assertEquals(listOf(SshLocalForwardRequest(8080)), requests) }
    }
    @Test fun capturedStartReadsCurrentValidatedDraft() {
        show(); val old = action(R.string.ssh_forward_start)
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("0")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { old(); assertTrue(requests.isEmpty()) }
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("5000")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { old(); assertEquals(listOf(SshLocalForwardRequest(5000)), requests) }
    }
}
