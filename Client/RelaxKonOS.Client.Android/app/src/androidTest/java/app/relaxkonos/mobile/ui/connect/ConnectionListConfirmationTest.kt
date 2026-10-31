package app.relaxkonos.mobile.ui.connect

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin

class ConnectionListConfirmationTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val visible = mutableStateOf(true)
    private val records = mutableStateOf(listOf(SavedLogin("https://confirmation.invalid", "review", 1, "Review")))
    private var mutations = 0
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        val container = (rule.activity.application as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            if (visible.value) ConnectionListScreen(records.value, emptyList(), { CredentialStatus.SavedByFingerprint },
                {}, {}, { mutations++ }, { mutations++ }, { visible.value = false }, onDeleteOwnerDevice = {})
        } } }
    }
    private fun open(deleting: Boolean) {
        val label = if (deleting) R.string.common_delete else R.string.connections_forget_password
        rule.onNodeWithContentDescription(text(R.string.connections_actions_title)).performClick()
        rule.onAllNodesWithText(text(label)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
        rule.onNodeWithText(text(if (deleting) R.string.connections_delete_title else R.string.connections_forget_title)).assertExists()
    }
    private fun callback(deleting: Boolean): () -> Boolean {
        val label = if (deleting) R.string.common_delete else R.string.connections_forget_password
        return requireNotNull(rule.onNode(hasText(text(label)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    }
    private fun verify(deleting: Boolean, boundary: String) {
        show(); open(deleting)
        val old = callback(deleting)
        when (boundary) {
            "removed" -> {
                val original = records.value
                rule.runOnIdle { records.value = emptyList() }
                rule.onNodeWithText(text(R.string.connections_empty)).assertExists()
                rule.runOnIdle { old(); assertEquals(0, mutations); records.value = original }
            }
            "disposed" -> {
                rule.runOnIdle { visible.value = false }
                rule.onNodeWithText(text(R.string.connections_title)).assertDoesNotExist()
                rule.runOnIdle { old(); assertEquals(0, mutations); visible.value = true }
            }
            else -> {
                rule.mainClock.autoAdvance = false
                try {
                    rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
                    rule.runOnUiThread { old(); assertEquals(0, mutations) }
                    rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
                } finally { rule.mainClock.autoAdvance = true }
            }
        }
        rule.onNodeWithText(text(if (deleting) R.string.connections_delete_title else R.string.connections_forget_title)).assertDoesNotExist()
        open(deleting)
        rule.runOnIdle { old(); assertEquals(0, mutations) }
        val fresh = callback(deleting)
        rule.runOnIdle { fresh(); fresh(); assertEquals(1, mutations) }
        rule.onNodeWithText(text(if (deleting) R.string.connections_delete_title else R.string.connections_forget_title)).assertDoesNotExist()
    }
    @Test fun removedRecordRejectsDelete() = verify(true, "removed")
    @Test fun removedRecordRejectsForget() = verify(false, "removed")
    @Test fun disposedPickerRejectsDelete() = verify(true, "disposed")
    @Test fun disposedPickerRejectsForget() = verify(false, "disposed")
    @Test fun pausedPickerRejectsDelete() = verify(true, "paused")
    @Test fun pausedPickerRejectsForget() = verify(false, "paused")
    private fun verifyCloseBeforeRecomposition(deleting: Boolean) {
        show()
        val close = requireNotNull(rule.onNodeWithText(text(R.string.common_close))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
        open(deleting)
        val old = callback(deleting)
        rule.mainClock.autoAdvance = false
        try { rule.runOnUiThread { close(); old(); assertEquals(0, mutations) } }
        finally { rule.mainClock.autoAdvance = true }
        rule.onNodeWithText(text(R.string.connections_title)).assertDoesNotExist()
    }
    @Test fun closeRejectsDeleteBeforeRecomposition() = verifyCloseBeforeRecomposition(true)
    @Test fun closeRejectsForgetBeforeRecomposition() = verifyCloseBeforeRecomposition(false)

}
