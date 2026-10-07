package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class TunnelProfileEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private val draft = TunnelProfileDraft(name = "review-profile", host = "frp.example.com")

    @Test fun failedSaveKeepsDraftAndShowsReasonInsideEditor() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var submitted = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {},
                { submitted++; state.value = state.value.copy(busy = true) }, {}, {})
        } }
        rule.onNodeWithText(text(R.string.tunnels_name)).performTextReplacement("edited-profile")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onAllNodesWithText(text(R.string.tunnels_confirm)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(R.string.tunnels_name)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, submitted); state.value = state.value.copy(busy = false, problemCode = "tunnel.revision_conflict") }
        rule.onNodeWithText(text(R.string.tunnels_conflict)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("edited-profile").assertExists()
        rule.runOnIdle { state.value = state.value.copy(problemCode = null, uncertain = true) }
        rule.onNodeWithText(text(R.string.tunnels_uncertain)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("edited-profile").assertExists()
    }

    @Test fun dirtyBackCanCancelOrExplicitlyDiscard() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var closed = false
        rule.setContent { MaterialTheme {
            if (!closed) TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) },
                { closed = true }, {}, {}, {})
        } }
        rule.onNodeWithText(text(R.string.tunnels_name)).performTextReplacement("edited-profile")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.tunnels_discard_confirm)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText("edited-profile").assertExists()
        rule.runOnIdle { assertFalse(closed) }
        Espresso.pressBack()
        rule.onAllNodesWithText(text(R.string.tunnels_confirm)).filter(hasClickAction()).onLast().performClick()
        rule.runOnIdle { assertTrue(closed) }
    }
}
