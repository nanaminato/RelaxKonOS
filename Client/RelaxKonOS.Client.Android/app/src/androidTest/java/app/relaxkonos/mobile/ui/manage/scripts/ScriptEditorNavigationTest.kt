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
    @get:Rule(order = 0) val diagnostics = app.relaxkonos.mobile.testing.ReviewWaitDiagnostics()
    @get:Rule(order = 1) val rule = createComposeRule()
    private val owner = SessionState.Active("https://example.com", "https://example.com", "user", "Workspace",
        emptySet(), "linux", ExecutionEligibility(true, null, false), workspaceId = "workspace")
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    @Composable private fun Editor(state: ScriptsUiState, onBack: () -> Unit,
        onSubmit: (app.relaxkonos.mobile.core.net.ScriptRequest) -> Unit) {
        var draft by remember { mutableStateOf(ScriptDraft(owner.userName)) }
        ScriptEditor(owner, state, draft, { draft = it }, onBack, onSubmit, {})
    }

    private fun click(id: Int) = rule.onNodeWithText(text(id)).fetchSemanticsNode()
        .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!

    @Test fun blankExecutionAccountUsesCurrentAccountWithoutApproval() {
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), ScriptDraft(" ", executable = "/bin/true", directory = "/tmp"), {}, {}, { submitted = it }, {})
        } }
        rule.onNodeWithText(text(R.string.guardian_admin_password)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(owner.userName, submitted?.runAs); assertNull(submitted?.approval) }
    }

    @Test fun windowsCaseVariantOfCurrentAccountDoesNotRequireApproval() {
        val windows = owner.copy(serverPlatform = "windows")
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme {
            ScriptEditor(windows, ScriptsUiState(), ScriptDraft("USER", executable = "C:\\job.exe", directory = "C:\\work"), {}, {}, { submitted = it }, {})
        } }
        rule.onNodeWithText(text(R.string.guardian_admin_password)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals("USER", submitted?.runAs); assertNull(submitted?.approval) }
    }

    @Test fun selectedPathsPreserveMeaningfulSpaces() {
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), ScriptDraft(owner.userName, executable = "/bin/job ", directory = "/work "), {}, {}, { submitted = it }, {})
        } }
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().performClick()
        rule.runOnIdle { assertEquals("/bin/job ", submitted?.executablePath); assertEquals("/work ", submitted?.workingDirectory) }
    }

    @Test fun linuxCaseVariantStillRequiresAdministratorApproval() {
        val draft = mutableStateOf(ScriptDraft("USER", executable = "/bin/true", directory = "/tmp"))
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        var approvalPassword = ""
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), draft.value, { draft.value = it }, {}, {
                submitted = it
                approvalPassword = it.approval?.password?.concatToString().orEmpty()
                it.approval?.password?.fill('\u0000')
            }, {})
        } }
        rule.onNodeWithText(text(R.string.guardian_admin_password)).assertExists()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.guardian_admin_name)).performScrollTo().performTextInput("administrator")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.guardian_admin_password)).performScrollTo().performTextInput("review-only")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle {
            assertEquals("USER", submitted?.runAs)
            assertEquals("administrator", submitted?.approval?.username)
            assertEquals("review-only", approvalPassword)
        }
        assertEquals("", rule.onNodeWithText(text(R.string.guardian_admin_password)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsProperties.EditableText].text)
    }

    @Test fun invalidAccountBlocksRunEvenWithApprovalAndCorrectionCanUseCurrentAccount() {
        val draft = mutableStateOf(ScriptDraft("-root", executable = "/bin/true", directory = "/tmp", adminName = "administrator"))
        var submitted: app.relaxkonos.mobile.core.net.ScriptRequest? = null
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), draft.value, { draft.value = it }, {}, { submitted = it }, {})
        } }
        rule.onNodeWithText(text(R.string.guardian_admin_password)).performScrollTo().performTextInput("review-only")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.scripts_run_as_invalid)).assertExists()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertNull(submitted) }
        rule.onNodeWithText(text(R.string.guardian_run_as)).performScrollTo().performTextReplacement(" ")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.guardian_admin_password)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(owner.userName, submitted?.runAs); assertNull(submitted?.approval) }
    }

    @Test fun nulPathBlocksRunUntilTheActualPathIsCorrected() {
        val draft = mutableStateOf(ScriptDraft(owner.userName, executable = "/bin/true\u0000", directory = "/tmp"))
        var submissions = 0
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), draft.value, { draft.value = it }, {}, { submissions++ }, {})
        } }
        rule.onNodeWithText(text(R.string.scripts_paths_invalid)).assertExists()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { draft.value = draft.value.copy(executable = "/bin/true", directory = "/tmp\u0000") }
        rule.onNodeWithText(text(R.string.scripts_run)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(0, submissions); draft.value = draft.value.copy(directory = "/tmp") }
        rule.onNodeWithText(text(R.string.scripts_paths_invalid)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.scripts_run)).performScrollTo().assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(1, submissions) }
    }

    @Test fun oldRunCannotSubmitChangedDraftAndCurrentRunIsConsumedOnce() {
        val draft = mutableStateOf(ScriptDraft(owner.userName, executable = "/bin/old", directory = "/tmp"))
        val requests = mutableListOf<app.relaxkonos.mobile.core.net.ScriptRequest>()
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), draft.value, { draft.value = it }, {}, { requests += it }, {})
        } }
        val old = click(R.string.scripts_run)
        rule.onNodeWithText(text(R.string.guardian_executable)).performScrollTo().performTextReplacement("/bin/current")
        Espresso.closeSoftKeyboard()
        val current = click(R.string.scripts_run)
        rule.runOnIdle { old(); assertTrue(requests.isEmpty()); current(); current() }
        rule.runOnIdle { assertEquals(listOf("/bin/current"), requests.map { it.executablePath }) }
    }

    @Test fun oldDiscardAndContinueCannotConsumeReopenedDecision() {
        var leaves = 0
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), ScriptDraft(owner.userName, executable = "/bin/true"), {}, { leaves++ }, {}, {})
        } }
        Espresso.pressBack()
        val discard = click(R.string.editor_discard_changes)
        val keep = click(R.string.common_cancel)
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        Espresso.pressBack()
        rule.runOnIdle { keep(); discard(); assertEquals(0, leaves) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        val current = click(R.string.editor_discard_changes)
        rule.runOnIdle { current(); current(); assertEquals(1, leaves) }
    }

    @Test fun replacingDraftRevokesOriginalDiscardDecision() {
        val draft = mutableStateOf(ScriptDraft(owner.userName, executable = "/bin/old"))
        var leaves = 0
        rule.setContent { MaterialTheme {
            ScriptEditor(owner, ScriptsUiState(), draft.value, { draft.value = it }, { leaves++ }, {}, {})
        } }
        Espresso.pressBack()
        val oldDiscard = click(R.string.editor_discard_changes)
        rule.runOnIdle { draft.value = draft.value.copy(executable = "/bin/current") }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
        rule.runOnIdle { oldDiscard(); assertEquals(0, leaves) }
        rule.onNodeWithText("/bin/current").assertExists()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.runOnIdle { assertEquals(1, leaves) }
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
        val state = mutableStateOf(ScriptsUiState())
        rule.setContent { MaterialTheme { Editor(state.value, {}, {
            submitted = it
            state.value = ScriptsUiState(error = true, problemCode = "scripts.invalid_request")
        }) } }
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
