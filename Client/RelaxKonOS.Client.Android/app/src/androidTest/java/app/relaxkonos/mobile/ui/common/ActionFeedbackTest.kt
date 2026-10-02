package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Column
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ActionFeedbackTest {
    @get:Rule val rule = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test fun failureAppearsAboveEditorAndDismissalPreservesDraft() {
        var message by mutableStateOf<String?>(null)
        var draft by mutableStateOf("unsaved settings")
        rule.setContent { MaterialTheme {
            OperationMessageDialog(message, onDismiss = { message = null })
            AlertDialog(onDismissRequest = {}, text = {
                OutlinedTextField(draft, { draft = it }, label = { Text("Draft") })
            }, confirmButton = { TextButton(onClick = { message = "Save rejected" }) { Text("Save") } })
        } }
        rule.onNodeWithText("Save").performClick()
        rule.onNodeWithText("Save rejected").assertIsDisplayed()
        rule.onAllNodes(isDialog()).assertCountEquals(2)
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.onAllNodes(isDialog()).assertCountEquals(1)
        rule.onNodeWithText("unsaved settings").assertIsDisplayed()
        rule.runOnIdle { assertEquals("unsaved settings", draft) }
        rule.onNodeWithText("Save").performClick()
        rule.onNodeWithText("Save rejected").assertIsDisplayed()
    }

    @Test fun warningRetryClosesDialogAndRunsOnce() {
        var attempts = 0
        var message by mutableStateOf<String?>("Result unknown")
        rule.setContent { MaterialTheme {
            OperationMessageDialog(message, tone = StatusTone.Warning,
                onDismiss = { message = null }, onRetry = { attempts++ })
        } }
        rule.onNodeWithText(context.getString(R.string.operation_warning_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.common_retry)).performClick()
        rule.onAllNodes(isDialog()).assertCountEquals(0)
        rule.runOnIdle { assertEquals(1, attempts) }
    }

    @Test fun acknowledgedOutcomeDoesNotReopenUntilAnotherAttempt() {
        var attempt by mutableIntStateOf(1)
        var unrelatedState by mutableIntStateOf(0)
        rule.setContent { MaterialTheme {
            Column {
                Text("refresh $unrelatedState")
                OperationMessageDialog("Request refused", eventKey = attempt)
            }
        } }
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.runOnIdle { unrelatedState++ }
        rule.onAllNodes(isDialog()).assertCountEquals(0)
        rule.runOnIdle { attempt++ }
        rule.onNodeWithText("Request refused").assertIsDisplayed()
    }

    @Test fun successStaysInline() {
        rule.setContent { MaterialTheme {
            ActionFeedback("Saved", onRetry = null, onDismiss = {}, tone = StatusTone.Success)
        } }
        rule.onNodeWithText("Saved").assertIsDisplayed()
        rule.onAllNodes(isDialog()).assertCountEquals(0)
    }
}
