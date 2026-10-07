package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ProxyEditorRecoveryTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun failedSubmissionAndListReloadKeepNameAndShowUncertainResult() {
        val profile = ProxyProfile("review-id", "original-name", "mihomo", false, 3)
        val state = mutableStateOf(ProxyState())
        var closed = false
        var submittedName = ""
        rule.setContent { MaterialTheme {
            ProxyEditor("profile", profile, state.value, { name, _, _ ->
                submittedName = name; state.value = state.value.copy(busy = true)
            }, { closed = true })
        } }
        rule.onNodeWithText(text(R.string.mihomo_name)).performTextReplacement("edited-name")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onNodeWithText(text(R.string.mihomo_name)).assertIsNotEnabled()
        rule.runOnIdle {
            assertEquals("edited-name", submittedName)
            state.value = state.value.copy(busy = false, uncertain = true, profiles = ApiResult.Transport(null))
        }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("edited-name").assertExists()
        rule.runOnIdle { assertFalse(closed) }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.common_cancel)).onLast().performClick()
        rule.onNodeWithText("edited-name").assertExists()
        rule.runOnIdle { assertFalse(closed) }
    }
}
