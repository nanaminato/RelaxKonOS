package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshLocalForwardRequest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshForwardEditorTest {
    @Test fun cleanupFailureAllowsRecoveryWithoutDiscardingDraftOrStartingAnotherForward() {
        val allowStart = mutableStateOf(false)
        val problem = mutableStateOf<String?>("cleanup")
        var cleanups = 0
        var request: SshLocalForwardRequest? = null
        rule.setContent { MaterialTheme {
            SshForwardEditor(SshLocalForwardRequest(5000, 18080), false, true, {}, { request = it },
                problem.value, allowStart.value, {
                    cleanups++; problem.value = null; allowStart.value = true
                })
        } }
        rule.onNodeWithText(text(R.string.ssh_forward_cleanup_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_stop_all)).performScrollTo().performClick()
        rule.onNodeWithText("18080").assertExists()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(1, cleanups); assertEquals(SshLocalForwardRequest(5000, 18080), request) }
    }
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun failedStartKeepsEditedRequestAndShowsFailureInsideTheEditor() {
        val ready = mutableStateOf(true)
        val problem = mutableStateOf<String?>(null)
        val requests = mutableListOf<SshLocalForwardRequest>()
        rule.setContent { MaterialTheme {
            SshForwardEditor(SshLocalForwardRequest(8080), false, ready.value, {}, {
                requests += it; ready.value = false
            }, problem.value)
        } }
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("5000")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).performClick()
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).assertIsNotEnabled()
        rule.runOnIdle { ready.value = true; problem.value = "connect" }
        rule.waitUntil(5_000) { rule.onNodeWithText(text(R.string.ssh_forward_connect_failed)).isDisplayed() }
        rule.onNodeWithText(text(R.string.ssh_forward_connect_failed)).assertIsDisplayed()
        rule.onNodeWithText("5000").assertExists()
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("5001")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).performClick()
        rule.runOnIdle { assertEquals(listOf(SshLocalForwardRequest(5000), SshLocalForwardRequest(5001)), requests) }
    }

    @Test fun changedDraftCanCancelDiscardAndBusyStateFreezesEveryAction() {
        val ready = mutableStateOf(true)
        val closed = mutableStateOf(false)
        var sends = 0
        rule.setContent { MaterialTheme {
            if (!closed.value) SshForwardEditor(SshLocalForwardRequest(8080), false, ready.value, { closed.value = true }, { sends++ })
            else Text("Parent page")
        } }
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("5000")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.waitUntil(5_000) { rule.onNodeWithText(text(R.string.ui_discard_draft_title)).isDisplayed() }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.common_cancel)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText("5000").assertExists()
        rule.runOnIdle { ready.value = false }
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_local_port)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_path)).assertIsNotEnabled()
        rule.onNodeWithText("HTTPS").assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed.value); assertEquals(0, sends); ready.value = true }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.runOnIdle { assertTrue(closed.value); assertEquals(0, sends) }
        rule.waitUntil(5_000) { rule.onAllNodesWithText(text(R.string.ssh_forward_start)).fetchSemanticsNodes().isEmpty() }
        rule.onNodeWithText("Parent page").assertIsDisplayed()
    }

    @Test fun invalidPortsAndPathCanBeCorrectedBeforeSubmittingExactRequest() {
        var request: SshLocalForwardRequest? = null
        rule.setContent { MaterialTheme {
            SshForwardEditor(SshLocalForwardRequest(8080), false, true, {}, { request = it })
        } }
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("0")
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_remote_port)).performTextReplacement("5000")
        rule.onNodeWithText(text(R.string.ssh_forward_local_port)).performTextReplacement("1023")
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_local_port)).performTextReplacement("18080")
        rule.onNodeWithText(text(R.string.ssh_forward_path)).performTextReplacement("//other.example")
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_forward_path)).performTextReplacement("/status?x=1")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText("HTTPS").performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.ssh_forward_start)).assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(SshLocalForwardRequest(5000, 18080, "https", "/status?x=1"), request) }
    }
}
