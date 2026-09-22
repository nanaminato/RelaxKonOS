package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.model.SavedConnection
import app.relaxkonos.mobile.ui.common.formatTimestamp

/**
 * The saved-connection picker shown from the sign-in screen.
 *
 * Whether a password is available is not stored here: it is asked of the connection vault, so the list
 * cannot claim a credential exists after the user removed it (`…V1.Design.md` §5.2, one source of
 * truth). The shell's own connections page shares the same rule.
 */
@Composable
fun ConnectionListScreen(
    profiles: List<SavedConnection>,
    hasCredential: (SavedConnection) -> Boolean,
    onSelected: (SavedConnection) -> Unit,
    onDelete: (SavedConnection) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.connections_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (profiles.isEmpty()) {
                    Text(stringResource(R.string.connections_empty))
                }
                profiles.forEach { profile ->
                    Card(Modifier.fillMaxWidth()) {
                        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(profile.serverUrl, style = MaterialTheme.typography.titleSmall)
                                Text(profile.identifier, style = MaterialTheme.typography.bodySmall)
                                Text(
                                    listOfNotNull(
                                        stringResource(
                                            if (hasCredential(profile)) {
                                                R.string.connections_saved_password
                                            } else {
                                                R.string.connections_no_password
                                            },
                                        ),
                                        formatTimestamp(profile.lastUsedEpochMillis),
                                    ).joinToString(" · "),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            TextButton(onClick = { onSelected(profile) }) { Text(stringResource(R.string.connections_use)) }
                            TextButton(onClick = { onDelete(profile) }) { Text(stringResource(R.string.common_delete)) }
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_close)) } },
    )
}
