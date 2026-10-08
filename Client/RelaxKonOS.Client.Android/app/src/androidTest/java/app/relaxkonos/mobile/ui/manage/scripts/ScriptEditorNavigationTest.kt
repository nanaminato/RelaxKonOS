package app.relaxkonos.mobile.ui.manage.scripts

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
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
    @Composable private fun Editor(state: ScriptsUiState, onBack: () -> Unit,
        onSubmit: (app.relaxkonos.mobile.core.net.ScriptRequest) -> Unit) {
        var draft by remember { mutableStateOf(ScriptDraft(owner.userName)) }
        ScriptEditor(owner, state, draft, { draft = it }, onBack, onSubmit, {})
    }

    @Test fun systemBackAllowsCancelThenExplicitDiscardWithoutSubmitting() {
        var left = false
        var submitted = false
        rule.setContent { MaterialTheme { Editor(ScriptsUiState(), { left = true }, { submitted = true }) } }
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
        rule.setContent { MaterialTheme { Editor(ScriptsUiState(), { left = true }, {}) } }
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.runOnIdle { assertTrue(left) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
    }

    @Test fun busyAndUnknownResultsKeepDraftAndPreventAnotherRun() {
        var left = false
        val state = mutableStateOf(ScriptsUiState())
        rule.setContent { MaterialTheme { Editor(state.value, { left = true }, {}) } }
        rule.onNodeWithText(text(R.string.guardian_executable)).performTextInput("/usr/bin/true")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { state.value = ScriptsUiState(loading = true) }
        rule.onNodeWithText(text(R.string.guardian_executable)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(left) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
        rule.runOnIdle { state.value = ScriptsUiState(error = true, problemCode = "guardian.agent_unavailable") }
        rule.onNodeWithText("/usr/bin/true").assertExists()
        rule.onNodeWithText(text(R.string.guardian_executable)).assertIsEnabled()
        rule.onNodeWithText(text(R.string.guardian_agent_failed)).assertExists()
        rule.runOnIdle { state.value = ScriptsUiState(error = true, problemCode = "scripts.write_unknown") }
        rule.onNodeWithText(text(R.string.scripts_write_unknown)).assertExists()
        rule.onNodeWithText(text(R.string.guardian_executable)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText("/usr/bin/true").assertExists()
        rule.runOnIdle { assertFalse(left) }
    }

    @Test fun duplicateEnvironmentBlocksRunAndCorrectionPreservesExactValue() {
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme { Editor(ScriptsUiState(), {}, { submitted = it }) } }
        rule.onNodeWithText(text(R.string.guardian_executable)).performTextInput("/bin/true")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.guardian_directory)).performScrollTo().performTextInput("/tmp")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_environment)).performScrollTo().performTextInput("KEY=first\nKEY=second")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_environment_invalid)).assertExists()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertNull(submitted) }
        rule.onNodeWithText(text(R.string.scripts_environment)).performScrollTo().performTextReplacement("KEY= spaced = value ")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_environment_invalid)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(mapOf("KEY" to " spaced = value "), submitted?.environment) }
    }

    @Test fun individualArgumentsPreserveEmptySpacesAndMultilineValuesAndRemovalOrder() {
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme { Editor(ScriptsUiState(), {}, { submitted = it }) } }
        rule.onNodeWithText(text(R.string.guardian_executable)).performTextInput("/bin/true")
        Espresso.closeSoftKeyboard()
        repeat(3) { rule.onNodeWithText(text(R.string.guardian_add_argument)).performScrollTo().performClick() }
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onNodeWithText(context.getString(R.string.guardian_argument_number, 2)).performScrollTo().performTextInput("first\nsecond")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(context.getString(R.string.guardian_argument_number, 3)).performScrollTo().performTextInput(" spaced ")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.guardian_directory)).performScrollTo().performTextInput("/tmp")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(listOf("", "first\nsecond", " spaced "), submitted?.arguments) }
        rule.onNodeWithContentDescription(context.getString(R.string.guardian_remove_argument, 2)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().performClick()
        rule.runOnIdle { assertEquals(listOf("", " spaced "), submitted?.arguments) }
    }
}
