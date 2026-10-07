package app.relaxkonos.mobile.ui.common

import androidx.compose.material3.*
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DraftCloseGuardTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun busyCloseIsIgnoredAndDirtyBackCanCancelOrDiscard() {
        val value = mutableStateOf("")
        val busy = mutableStateOf(false)
        var closed = false
        rule.setContent { MaterialTheme {
            DraftCloseGuard(!busy.value, value.value.isNotEmpty(), { closed = true }) { close ->
                AlertDialog(onDismissRequest = close, text = { OutlinedTextField(value.value, { value.value = it }, label = { Text("Draft") }) },
                    confirmButton = { TextButton(onClick = close) { Text("Close") } })
            }
        } }
        rule.onNodeWithText("Draft").performTextInput("edited")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { busy.value = true }
        rule.onNodeWithText("Close").performClick()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed); busy.value = false }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText("edited").assertExists()
        rule.runOnIdle { assertFalse(closed) }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.editor_discard_changes)).performClick()
        rule.runOnIdle { assertTrue(closed) }
    }

    @Test fun unchangedDraftClosesWithoutConfirmation() {
        var closed = false
        rule.setContent { MaterialTheme {
            DraftCloseGuard(true, false, { closed = true }) { close ->
                AlertDialog(onDismissRequest = close, text = { Text("Unchanged") }, confirmButton = {})
            }
        } }
        Espresso.pressBack()
        rule.runOnIdle { assertTrue(closed) }
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
    }
}
