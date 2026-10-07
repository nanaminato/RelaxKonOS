package app.relaxkonos.mobile.ui.manage.scripts

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ScriptEditorNavigationTest {
    @get:Rule val rule = createComposeRule()
    private val owner = SessionState.Active("https://example.com", "https://example.com", "user", "Workspace",
        emptySet(), "linux", ExecutionEligibility(true, null, false), workspaceId = "workspace")
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun systemBackAllowsCancelThenExplicitDiscardWithoutSubmitting() {
        var left = false
        var submitted = false
        rule.setContent { MaterialTheme { ScriptEditor(owner, { left = true }, { submitted = true }) } }
        rule.onNodeWithText(text(R.string.guardian_executable)).performTextInput("/usr/bin/true")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText("/usr/bin/true").assertExists()
        rule.runOnIdle { assertFalse(left); assertFalse(submitted) }
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.runOnIdle { assertTrue(left); assertFalse(submitted) }
    }

    @Test fun emptyDraftReturnsWithoutConfirmation() {
        var left = false
        rule.setContent { MaterialTheme { ScriptEditor(owner, { left = true }, {}) } }
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.runOnIdle { assertTrue(left) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
    }
}
