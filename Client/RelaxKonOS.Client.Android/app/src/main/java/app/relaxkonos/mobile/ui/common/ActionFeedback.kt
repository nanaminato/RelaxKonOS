package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.AlertDialog
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/** Action failures and warnings interrupt with a dialog; successful outcomes stay inline. */
@Composable
fun ActionFeedback(
    message: String,
    onRetry: (() -> Unit)?,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    tone: StatusTone = StatusTone.Danger,
) {
    if (tone == StatusTone.Danger || tone == StatusTone.Warning) {
        OperationMessageDialog(message, tone = tone, onRetry = onRetry, onDismiss = onDismiss)
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
                Text(message, style = MaterialTheme.typography.bodyMedium)
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
 */
@Composable
fun OperationMessageDialog(
    message: String?,
    eventKey: Any? = message,
    tone: StatusTone = StatusTone.Danger,
    onRetry: (() -> Unit)? = null,
    onDismiss: (() -> Unit)? = null,
) {
    var visible by remember(message, eventKey) { mutableStateOf(false) }
    LaunchedEffect(message, eventKey) { visible = message != null }
    if (!visible || message == null) return
    val dismiss = { visible = false; onDismiss?.invoke(); Unit }
    AlertDialog(
        onDismissRequest = dismiss,
        icon = { DesktopIcon(icon = DesktopIcons.notice, size = 24.dp) },
        title = { Text(stringResource(if (tone == StatusTone.Warning) R.string.operation_warning_title else R.string.operation_error_title)) },
        text = { Text(message, Modifier.verticalScroll(rememberScrollState())) },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_dismiss)) } },
        dismissButton = { if (onRetry != null) TextButton(onClick = { dismiss(); onRetry() }) { Text(stringResource(R.string.common_retry)) } },
    )
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
