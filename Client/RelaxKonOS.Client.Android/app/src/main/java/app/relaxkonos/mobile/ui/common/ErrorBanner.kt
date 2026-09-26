package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Inline banner for a reported outcome.
 *
 * Only the mapped, localised sentence is rendered: the raw RFC 7807 `type` URI, the problem code and
 * the server's English `detail` stay out of the UI (`RelaxKonOS.Mobile.V1.Design.md` §8).
 *
 * [tone] defaults to danger because a message worth interrupting the page for is usually a refusal,
 * but a completed action reports itself through the same banner with `StatusTone.Success`: one
 * component means its layout, dismissing and retry affordances cannot drift between the two cases.
 *
 * It carries no outer padding of its own. The banner appears both inside an already-padded page column
 * and floating over the whole window, so the inset belongs to the caller — a fixed one here would
 * double up in the first case and be missing in the second.
 */
@Composable
fun ErrorBanner(
    message: String,
    onRetry: (() -> Unit)?,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    tone: StatusTone = StatusTone.Danger,
) {
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
 * It is deliberately not an [ErrorBanner]: nothing has failed yet, there is nothing to retry, and a
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
