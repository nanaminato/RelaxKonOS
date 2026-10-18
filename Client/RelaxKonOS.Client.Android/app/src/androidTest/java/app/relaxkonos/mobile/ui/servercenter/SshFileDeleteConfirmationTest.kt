package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshFileEntry
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshFileDeleteConfirmationTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val first = listOf(SshFileEntry("/first", "first", false, false, 1))
    private val pending = mutableStateOf(first)
    private val sent = mutableListOf<List<SshFileEntry>>()
    private fun show() = rule.setContent { MaterialTheme {
        SshFileDeleteConfirmation(pending.value, false, { sent += it }, {})
    } }
    private fun callback() = requireNotNull(rule.onAllNodesWithText(rule.activity.getString(R.string.ssh_files_delete))
        .filter(hasClickAction()).onLast().fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    @Test fun capturedConfirmationAddressesDisplayedEntriesInsteadOfNewSelection() {
        show(); val old = callback()
        val second = listOf(SshFileEntry("/second", "second", false, false, 1))
        rule.runOnIdle { pending.value = second }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_delete_note, "second")).assertIsDisplayed()
        rule.runOnIdle { old(); assertSame(first, sent.single()) }
        val fresh = callback()
        rule.runOnIdle { fresh(); assertSame(second, sent.last()) }
    }
    @Test fun pausedConfirmationCannotDispatchDeleteBeforeComposeUpdates() {
        show(); val old = callback()
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { old(); assertTrue(sent.isEmpty()) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
    }
}
