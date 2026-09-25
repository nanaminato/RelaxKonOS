package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Progress for a long transfer.
 *
 * It is a card pinned to the bottom of the content area rather than a modal sheet, because
 * `RelaxKonOS.Mobile.V1.Design.md` §3.4 requires long operations to stay collapsible and to never
 * block navigation. [onCollapse] hides the detail line; the transfer itself is unaffected.
 *
 * The surface is raised above the page rather than outlined like [SectionCard]: this one genuinely
 * floats over a list the user can keep scrolling, so it has to separate from a busy background.
 *
 * [footnote] carries a second line under the bar — the byte counter for a transfer, or the reason one
 * stopped. [actions] is for the case where the transfer is no longer running and the only useful
 * controls are the ones that decide its fate, such as "continue" and "discard"; a running transfer
 * offers [onCancel] and nothing else, because there is nothing else to decide yet.
 */
@Composable
fun ProgressSheet(
    title: String,
    detail: String?,
    progress: Float?,
    collapsed: Boolean,
    onCollapsedChange: (Boolean) -> Unit,
    onCancel: (() -> Unit)?,
    modifier: Modifier = Modifier,
    footnote: String? = null,
    actions: (@Composable RowScope.() -> Unit)? = null,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.lg),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        shadowElevation = 6.dp,
    ) {
        Column(Modifier.padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
            ) {
                DesktopIcon(icon = DesktopIcons.refresh, size = 20.dp)
                Text(title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                TextButton(onClick = { onCollapsedChange(!collapsed) }) {
                    Text(
                        stringResource(
                            if (collapsed) R.string.common_expand else R.string.common_collapse,
                        ),
                    )
                }
                if (onCancel != null) {
                    TextButton(onClick = onCancel) { Text(stringResource(R.string.common_cancel)) }
                }
            }
            if (!collapsed) {
                if (detail != null) {
                    Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (progress == null) {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                } else {
                    LinearProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
                }
                if (footnote != null) {
                    Text(
                        footnote,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (!collapsed && actions != null) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    actions()
                }
            }
        }
    }
}
