package app.relaxkonos.mobile.ui.manage.docker

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

class DockerComposerNavigationTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private val initial = "services:\n  app:\n    image: nginx:alpine\n"

    @Test fun recompositionPreservesDraftAndBackRequiresExplicitDiscard() {
        val busy = mutableStateOf(false)
        var left = false
        var deployed = false
        rule.setContent {
            MaterialTheme {
                DockerComposer(initial, null, busy.value, { _, _ -> }, {},
                    { left = true }, { _, _ -> deployed = true })
            }
        }
        rule.onNodeWithText(text(R.string.docker_stack_name)).performTextInput("test-project")
        rule.onNodeWithText(text(R.string.docker_compose_yaml)).performTextReplacement("services:\n  edited: {}\n")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { busy.value = true }
        rule.onNodeWithText(text(R.string.docker_stack_name)).assertIsNotEnabled()
        rule.onNodeWithText("test-project").assertExists()
        rule.runOnIdle { busy.value = false }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        // Both the editor footer and the topmost confirmation contain a Cancel action.
        rule.onAllNodesWithText(text(R.string.common_cancel)).onLast().performClick()
        rule.onNodeWithText("test-project").assertExists()
        rule.onNodeWithText("services:\n  edited: {}\n").assertExists()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.runOnIdle { assertTrue(left); assertFalse(deployed) }
    }

    @Test fun unchangedDraftCanCloseImmediately() {
        var left = false
        rule.setContent { MaterialTheme { DockerComposer(initial, null, false, { _, _ -> }, {}, { left = true }, { _, _ -> }) } }
        Espresso.pressBack()
        rule.runOnIdle { assertTrue(left) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
    }
}
