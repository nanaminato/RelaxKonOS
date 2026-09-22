package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R

/**
 * Inline banner for a failed call.
 *
 * Only the mapped, localised sentence is rendered: the raw RFC 7807 `type` URI, the problem code and
 * the server's English `detail` stay out of the UI (`RelaxKonOS.Mobile.V1.Design.md` §8).
 */
@Composable
fun ErrorBanner(message: String, onRetry: (() -> Unit)?, onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    Card(modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(message, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (onRetry != null) {
                    TextButton(onClick = onRetry) { Text(stringResource(R.string.common_retry)) }
                }
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_dismiss)) }
            }
        }
    }
}

/** Shown when a destination exists but the server does not advertise the capability behind it. */
@Composable
fun CapabilityMissingNotice(modifier: Modifier = Modifier) {
    Text(
        text = stringResource(R.string.error_capability_missing),
        modifier = modifier.padding(16.dp),
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        style = MaterialTheme.typography.bodyMedium,
    )
}
