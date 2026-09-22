package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R

/**
 * Progress for a long transfer.
 *
 * It is a card pinned to the bottom of the content area rather than a modal sheet, because
 * `RelaxKonOS.Mobile.V1.Design.md` §3.4 requires long operations to stay collapsible and to never
 * block navigation. [onCollapse] hides the detail line; the transfer itself is unaffected.
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
) {
    Card(modifier = modifier.fillMaxWidth().padding(16.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
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
            }
        }
    }
}
