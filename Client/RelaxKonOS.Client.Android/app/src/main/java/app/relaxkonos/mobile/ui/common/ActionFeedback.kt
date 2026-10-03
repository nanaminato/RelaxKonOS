package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.data.ReminderPreference
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/** Action failures and warnings interrupt with a dialog; successful outcomes stay inline.
 *
 * [reminders] is the device-local preference behind [UiMessage.reminder]: pass it and a low-impact
 * reminder carries a "do not remind me again" checkbox, and one that is already silenced is not
 * rendered at all. Omit it and the message behaves exactly as it did before — no offer, and no
 * suppression — which is the honest degradation for a caller that cannot persist the choice.
 */
@Composable
fun ActionFeedback(
    message: UiMessage,
    onRetry: (() -> Unit)?,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    reminders: ReminderPreference? = null,
) {
    val reminder = message.reminder
    val preference = reminders
    if (reminder != null && preference != null && preference.isSilenced(reminder)) {
        // Nothing to acknowledge: the user has already answered this exact sentence on this device.
        return
    }
    val tone = message.tone
    val text = message.text()
    if (tone == StatusTone.Danger || tone == StatusTone.Warning) {
        OperationMessageDialog(
            text,
            eventKey = message,
            tone = tone,
            onRetry = onRetry,
            onDismiss = onDismiss,
            // With no preference there is no offer: a checkbox whose answer cannot be stored is worse
            // than being reminded once more.
            reminder = if (preference == null) null else reminder,
            onSilenceReminder = if (preference == null) null else { kind -> preference.silence(kind) },
        )
        return
    }
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.md),
        color = toneContainer(tone),
        contentColor = toneContent(tone),
    ) {
        Row(
            Modifier.padding(Spacing.md),
            horizontalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            DesktopIcon(icon = DesktopIcons.notice, size = 22.dp)
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(text, style = MaterialTheme.typography.bodyMedium)
                Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    if (onRetry != null) {
                        TextButton(
                            onClick = onRetry,
                            colors = ButtonDefaults.textButtonColors(contentColor = toneContent(tone)),
                        ) { Text(stringResource(R.string.common_retry)) }
                    }
                    TextButton(
                        onClick = onDismiss,
                        colors = ButtonDefaults.textButtonColors(contentColor = toneContent(tone)),
                    ) { Text(stringResource(R.string.common_dismiss)) }
                }
            }
        }
    }
}

/** Dismissal acknowledges the message without discarding a draft or unresolved operation facts.
 * [eventKey] distinguishes repeated outcomes with the same wording (for example after retry).
 * Opening after composition keeps feedback above an already open editor dialog.
 *
 * [reminder] turns the acknowledgment into a choice: the user may silence this sentence on this device
 * and dismiss in one gesture. The offer is only rendered when both the kind and its effect are
 * supplied, so a caller that cannot persist the choice simply never shows the checkbox.
 */
@Composable
fun OperationMessageDialog(
    message: String?,
    eventKey: Any? = message,
    tone: StatusTone = StatusTone.Danger,
    onRetry: (() -> Unit)? = null,
    onDismiss: (() -> Unit)? = null,
    reminder: ReminderKind? = null,
    onSilenceReminder: ((ReminderKind) -> Unit)? = null,
) {
    var visible by remember(message, eventKey, tone) { mutableStateOf(false) }
    var doNotRemind by remember(message, eventKey, tone) { mutableStateOf(false) }
    LaunchedEffect(message, eventKey, tone) { visible = message != null }
    if (!visible || message == null) return
    val silences = reminder != null && onSilenceReminder != null
    val dismiss = {
        // Silencing is part of the acknowledgment, not a separate step: a checkbox the user ticked and
        // then had to confirm twice would be a worse contract than no checkbox at all.
        if (doNotRemind && reminder != null) onSilenceReminder?.invoke(reminder)
        visible = false
        onDismiss?.invoke()
        Unit
    }
    AlertDialog(
        onDismissRequest = dismiss,
        icon = { DesktopIcon(icon = DesktopIcons.notice, size = 24.dp) },
        title = { Text(stringResource(operationMessageTitle(tone))) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(message)
                if (silences) {
                    // The whole row is the target and the label is centred against the box, so a
                    // two-line label reads as one control rather than a checkbox with a caption.
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = Spacing.sm)
                            .toggleable(
                                value = doNotRemind,
                                role = Role.Checkbox,
                                onValueChange = { doNotRemind = it },
                            ),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Checkbox(checked = doNotRemind, onCheckedChange = null)
                        Text(
                            stringResource(R.string.notice_do_not_remind),
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(start = Spacing.sm),
                        )
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_dismiss)) } },
        dismissButton = { if (onRetry != null) TextButton(onClick = { dismiss(); onRetry() }) { Text(stringResource(R.string.common_retry)) } },
    )
}

internal fun operationMessageTitle(tone: StatusTone): Int = when (tone) {
    StatusTone.Danger -> R.string.operation_error_title
    StatusTone.Warning -> R.string.operation_warning_title
    StatusTone.Success -> R.string.operation_success_title
    StatusTone.Neutral, StatusTone.Primary, StatusTone.Info -> R.string.operation_info_title
}

/** Shown when a destination exists but the server does not advertise the capability behind it. */
@Composable
fun CapabilityMissingNotice(modifier: Modifier = Modifier) {
    Text(
        text = stringResource(R.string.error_capability_missing),
        modifier = modifier.padding(Spacing.lg),
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        style = MaterialTheme.typography.bodyMedium,
    )
}

/**
 * The standing notice a session carries when the server has already said this identity may not run
 * ordinary operations.
 *
 * It is deliberately not [ActionFeedback]: nothing has failed yet, there is nothing to retry, and a
 * fact about the session cannot be dismissed away. It wears the warning tone rather than the danger
 * one because the session itself is perfectly usable — metrics, processes and the rest keep working;
 * only the file, terminal and Git surfaces the user is most likely to reach for next do not.
 *
 * [reason] is the server's stable reason code, never its text; the sentence comes from the client's
 * own packs through [executionEligibilityMessage].
 */
@Composable
fun ExecutionEligibilityNotice(reason: String?, privilegedFilesAvailable: Boolean = false, modifier: Modifier = Modifier) {
    val tone = StatusTone.Warning
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.md),
        color = toneContainer(tone),
        contentColor = toneContent(tone),
    ) {
        Row(
            Modifier.padding(Spacing.md),
            horizontalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            DesktopIcon(icon = DesktopIcons.notice, size = 22.dp)
            Text(
                (if (privilegedFilesAvailable) UiMessage(R.string.execution_root_files_available)
                 else executionEligibilityMessage(reason)).text(),
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}
