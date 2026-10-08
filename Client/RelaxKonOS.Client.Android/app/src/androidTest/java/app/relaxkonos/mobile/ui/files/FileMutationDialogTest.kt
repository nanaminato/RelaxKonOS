package app.relaxkonos.mobile.ui.files

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.UiMessage
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class FileMutationDialogTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun createBusyThenRefusalKeepsNameAndAllowsCorrection() = nameFailureContract(false)
    @Test fun renameBusyThenRefusalKeepsNameAndAllowsCorrection() = nameFailureContract(true)

    private fun nameFailureContract(rename: Boolean) {
        val busy = mutableStateOf(false)
        val error = mutableStateOf<UiMessage?>(null)
        var closed = false
        var submitted: String? = null
        rule.setContent { MaterialTheme {
            if (rename) RenameDialog(RemoteEntry("/tmp/original", "original", false, 0, null, null),
                !busy.value, { closed = true }, { submitted = it }, busy.value, error.value)
            else NewDirectoryDialog("/tmp", !busy.value, { closed = true }, { submitted = it }, busy.value, error.value)
        } }
        val field = rule.onNodeWithText(text(R.string.files_label_name))
        val confirm = rule.onNodeWithText(text(if (rename) R.string.common_save else R.string.common_create))
        field.performTextReplacement("review name")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { busy.value = true }
        field.assertIsNotEnabled()
        confirm.assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed); assertNull(submitted); busy.value = false; error.value = UiMessage(R.string.error_generic) }
        rule.onNodeWithText("review name").assertExists()
        rule.onNodeWithText(text(R.string.error_generic)).assertIsDisplayed()
        field.performTextReplacement("corrected name")
        Espresso.closeSoftKeyboard()
        confirm.performClick()
        rule.runOnIdle { assertEquals("corrected name", submitted); assertFalse(closed) }
    }

    @Test fun directoryCopyBlocksInvalidPathsAndPreservesDestination() = transferContract(false)
    @Test fun directoryMoveBlocksInvalidPathsAndPreservesDestination() = transferContract(true)

    private fun transferContract(move: Boolean) {
        val allowed = mutableStateOf(true)
        var submitted: String? = null
        val path = "/tmp/" + "long-directory/".repeat(30) + "source"
        val entry = RemoteEntry(path, "source", true, null, null, null)
        rule.setContent { MaterialTheme {
            TransferDialog(TransferTarget(entry, move), allowed.value, {}, { submitted = it })
        } }
        val field = rule.onNodeWithText(text(R.string.files_destination_path))
        val confirm = rule.onNode(hasText(text(if (move) R.string.files_action_move else R.string.files_action_copy)) and hasClickAction())
        confirm.assertIsNotEnabled()
        field.performScrollTo().performTextReplacement("relative/path")
        confirm.assertIsNotEnabled()
        field.performTextReplacement("$path/child")
        confirm.assertIsNotEnabled()
        field.performTextReplacement("/tmp/target/source")
        confirm.assertIsEnabled()
        rule.runOnIdle { allowed.value = false }
        confirm.assertIsNotEnabled()
        rule.onNodeWithText("/tmp/target/source").assertExists()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsDisplayed().assertIsEnabled()
        rule.runOnIdle { allowed.value = true }
        confirm.assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals("/tmp/target/source", submitted) }
    }

    @Test fun longRenamePathKeepsChangedNameAndActionsAccessible() {
        var submitted: String? = null
        val entry = RemoteEntry("/tmp/" + "long-directory/".repeat(30) + "original", "original", false, 0, null, null)
        rule.setContent { MaterialTheme {
            RenameDialog(entry, true, {}, { submitted = it })
        } }
        rule.onNodeWithText(text(R.string.files_label_name)).performScrollTo().performTextReplacement("renamed")
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_save)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals("renamed", submitted) }
    }

    @Test fun longParentKeepsNameAndActionsAccessibleWithKeyboard() {
        var submitted: String? = null
        val path = "/tmp/" + "long-directory/".repeat(30)
        rule.setContent { MaterialTheme {
            NewDirectoryDialog(path, true, {}, { submitted = it })
        } }
        rule.onNodeWithText(text(R.string.files_label_name)).performScrollTo().performTextInput("review")
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_create)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals("review", submitted) }
    }

    @Test fun permissionBusyBlocksEditingAndBackThenAllowsCancelAfterRecovery() {
        val busy = mutableStateOf(true)
        var closed = false
        var saved = false
        rule.setContent { MaterialTheme {
            FilePermissionEditor("/tmp/review", true, "4755", true, busy.value, true, null,
                {}, {}, { saved = true }, { closed = true })
        } }
        rule.onNodeWithText(text(R.string.files_permissions_octal)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.files_permissions_recursive)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed); assertFalse(saved); busy.value = false }
        rule.onNodeWithText("4755").assertExists()
        rule.onNodeWithText(text(R.string.files_permissions_recursive)).assertIsOn()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(closed); assertFalse(saved) }
    }

    @Test fun permissionFailureKeepsInputAndRecursiveScopeAndAllowsCorrection() {
        val input = mutableStateOf("4755")
        val recursive = mutableStateOf(true)
        val failure = mutableStateOf<UiMessage?>(null)
        var saved: Pair<String, Boolean>? = null
        rule.setContent { MaterialTheme {
            FilePermissionEditor("/tmp/review", true, input.value, recursive.value, false, true, failure.value,
                { input.value = it }, { recursive.value = it }, { saved = input.value to recursive.value }, {})
        } }
        rule.runOnIdle { failure.value = UiMessage(R.string.files_read_failed) }
        rule.onNodeWithText(text(R.string.files_read_failed)).assertExists()
        rule.onNodeWithText("4755").assertExists()
        rule.onNodeWithText(text(R.string.files_permissions_recursive)).assertIsOn()
        rule.onNodeWithText(text(R.string.files_permissions_octal)).performTextReplacement("888")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.files_permissions_octal)).performTextReplacement("0750")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.runOnIdle { assertEquals("0750" to true, saved) }
    }

    @Test fun createKeepsInputWhileWriteIsPausedAndResumesWithoutReset() {
        val allowed = mutableStateOf(true)
        var submitted: String? = null
        rule.setContent { MaterialTheme {
            NewDirectoryDialog("/tmp", allowed.value, {}, { submitted = it })
        } }
        rule.onNodeWithText(text(R.string.files_label_name)).performTextInput("review folder")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_create)).assertIsEnabled()
        rule.runOnIdle { allowed.value = false }
        rule.onNodeWithText(text(R.string.common_create)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        rule.onNodeWithText("review folder").assertExists()
        rule.runOnIdle { assertNull(submitted); allowed.value = true }
        rule.onNodeWithText(text(R.string.common_create)).performClick()
        rule.runOnIdle { assertEquals("review folder", submitted) }
    }

    @Test fun renameKeepsChangedNameAndAllowsCancelWhileWriteIsPaused() {
        val allowed = mutableStateOf(true)
        var dismissed = false
        var submitted = false
        val entry = RemoteEntry("/tmp/original", "original", false, 0, null, null)
        rule.setContent { MaterialTheme {
            RenameDialog(entry, allowed.value, { dismissed = true }, { submitted = true })
        } }
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.files_label_name)).performTextReplacement("renamed")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).assertIsEnabled()
        rule.runOnIdle { allowed.value = false }
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.onNodeWithText("renamed").assertExists()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(dismissed); assertFalse(submitted) }
    }

    @Test fun disabledDeleteConfirmationStillAllowsCancel() {
        var dismissed = false
        var submitted = false
        rule.setContent { MaterialTheme {
            ConfirmDangerousDialog("Delete", "/tmp/review", "Confirm",
                { submitted = true }, { dismissed = true }, confirmEnabled = false)
        } }
        rule.onNodeWithText("Confirm").assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(dismissed); assertFalse(submitted) }
    }
}
