package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.model.SavedConnection
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp

/**
 * Connections, inside the shell.
 *
 * The same content as the sign-in list, with one difference that only makes sense while signed in:
 * removing a connection also removes the password saved for it. Switching to another server is not
 * offered here — that would mean tearing down the live session, and the design keeps sign-out as the
 * single, explicit way to do that.
 */
@Composable
fun ConnectionsScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    var revision by remember { mutableStateOf(0) }
    var deleteTarget by remember { mutableStateOf<SavedConnection?>(null) }

    val profiles = remember(revision) { container.profiles.all() }
    val activeServerUrl = container.session.serverUrl

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }
        Text(stringResource(R.string.connections_title), style = MaterialTheme.typography.headlineSmall)

        SectionCard(stringResource(R.string.connections_title)) {
            if (profiles.isEmpty()) {
                EmptyHint(stringResource(R.string.connections_empty))
            } else {
                profiles.forEach { profile ->
                    val hasCredential = container.vault.record(VaultKind.Connection, profile.serverUrl, profile.identifier) != null
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(profile.serverUrl, style = MaterialTheme.typography.bodyLarge)
                            Text(
                                listOfNotNull(
                                    profile.identifier,
                                    formatTimestamp(profile.lastUsedEpochMillis),
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(
                                stringResource(
                                    if (hasCredential) R.string.connections_saved_password else R.string.connections_no_password,
                                ),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            if (profile.serverUrl == activeServerUrl) {
                                Text(
                                    stringResource(R.string.connections_active),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.primary,
                                )
                            }
                        }
                        TextButton(onClick = { deleteTarget = profile }) { Text(stringResource(R.string.common_delete)) }
                    }
                }
            }
        }

        Text(
            stringResource(R.string.connections_sign_out_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }

    deleteTarget?.let { profile ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_delete_title),
            message = stringResource(R.string.connections_delete_message, profile.serverUrl, profile.identifier),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                container.vault.delete(VaultKind.Connection, profile.serverUrl, profile.identifier)
                container.profiles.remove(profile.serverUrl)
                revision++
            },
            onDismiss = { deleteTarget = null },
        )
    }
}
