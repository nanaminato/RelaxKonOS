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
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.data.ReminderPreference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
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
            ActionFeedback(UiMessage(R.string.files_uploaded, listOf("file.png"), tone = StatusTone.Success), onRetry = null, onDismiss = {})
        } }
        rule.onNodeWithText(context.getString(R.string.files_uploaded, "file.png")).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertDoesNotExist()
        rule.onAllNodes(isDialog()).assertCountEquals(0)
    }

    @Test fun structuredWarningRetainsItsSeverity() {
        rule.setContent { MaterialTheme {
            ActionFeedback(UiMessage(R.string.login_credential_not_saved, tone = StatusTone.Warning), onRetry = null, onDismiss = {})
        } }
        rule.onNodeWithText(context.getString(R.string.operation_warning_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertDoesNotExist()
    }

    @Test fun informationalNoticeStaysInline() {
        rule.setContent { MaterialTheme {
            ActionFeedback(UiMessage(R.string.operation_info_title, tone = StatusTone.Info), onRetry = null, onDismiss = {})
        } }
        rule.onNodeWithText(context.getString(R.string.operation_info_title)).assertIsDisplayed()
        rule.onAllNodes(isDialog()).assertCountEquals(0)
    }

    @Test fun sameTextWithChangedSeverityUsesTheNewTitle() {
        var tone by mutableStateOf(StatusTone.Danger)
        rule.setContent { MaterialTheme { OperationMessageDialog("Same message", tone = tone) } }
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.runOnIdle { tone = StatusTone.Success }
        rule.onNodeWithText(context.getString(R.string.operation_success_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertDoesNotExist()
        rule.runOnIdle { tone = StatusTone.Info }
        rule.onNodeWithText(context.getString(R.string.operation_info_title)).assertIsDisplayed()
    }

    // ---- Do not remind me again -------------------------------------------------------------

    @Test fun aDeviceReminderOffersSilencingAndSilencesOnAcknowledge() {
        val reminders = FakeReminderPreference()
        rule.setContent { MaterialTheme {
            ActionFeedback(reminderMessage(), onRetry = null, onDismiss = {}, reminders = reminders)
        } }
        rule.onNodeWithText(context.getString(R.string.notice_do_not_remind)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.notice_do_not_remind)).performClick()
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.runOnIdle { assertEquals(setOf(ReminderKind.SavedPasswordUnavailable), reminders.silenced) }
    }

    @Test fun acknowledgingWithoutTickingSilencesNothing() {
        val reminders = FakeReminderPreference()
        rule.setContent { MaterialTheme {
            ActionFeedback(reminderMessage(), onRetry = null, onDismiss = {}, reminders = reminders)
        } }
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.runOnIdle { assertTrue(reminders.silenced.isEmpty()) }
    }

    @Test fun aSilencedReminderIsNotRenderedAtAll() {
        val reminders = FakeReminderPreference(setOf(ReminderKind.SavedPasswordUnavailable))
        rule.setContent { MaterialTheme {
            ActionFeedback(reminderMessage(), onRetry = null, onDismiss = {}, reminders = reminders)
        } }
        rule.onAllNodes(isDialog()).assertCountEquals(0)
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertDoesNotExist()
    }

    @Test fun aMessageWithoutAReminderOffersNothing() {
        val reminders = FakeReminderPreference()
        rule.setContent { MaterialTheme {
            ActionFeedback(
                UiMessage(R.string.login_credential_not_saved, tone = StatusTone.Warning),
                onRetry = null,
                onDismiss = {},
                reminders = reminders,
            )
        } }
        rule.onNodeWithText(context.getString(R.string.operation_warning_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.notice_do_not_remind)).assertDoesNotExist()
    }

    @Test fun aCallerWithoutThePreferenceStillSeesTheMessageButCannotSilenceIt() {
        rule.setContent { MaterialTheme {
            ActionFeedback(reminderMessage(), onRetry = null, onDismiss = {})
        } }
        rule.onNodeWithText(context.getString(R.string.operation_error_title)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.notice_do_not_remind)).assertDoesNotExist()
    }

    private fun reminderMessage() = UiMessage(
        R.string.login_credential_not_saved_reason,
        listOf(context.getString(R.string.vault_failure_unavailable)),
        reminder = ReminderKind.SavedPasswordUnavailable,
    )
}

/** Records what the dialog asked to silence; the persistence itself is covered off-device. */
private class FakeReminderPreference(private var stored: Set<ReminderKind> = emptySet()) : ReminderPreference {
    val silenced: Set<ReminderKind> get() = stored

    override fun isSilenced(kind: ReminderKind): Boolean = kind in stored

    override fun silence(kind: ReminderKind) {
        stored = stored + kind
    }
}
