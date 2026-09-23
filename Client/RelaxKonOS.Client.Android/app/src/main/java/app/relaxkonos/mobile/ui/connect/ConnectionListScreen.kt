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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.formatTimestamp

/**
 * The saved-login picker shown from the sign-in screen.
 *
 * Each row offers two actions that never stand in for each other
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §6.3): **forget the password** drops the credential
 * and keeps the login, **delete login** drops both. Both address exactly one row — nothing here acts on
 * a server address alone, because one server can hold several accounts and their records are independent.
 *
 * Whether a password is saved is asked of the vault rather than read from the profile list, so the list
 * cannot claim a credential the user already removed (§4.2).
 */
@Composable
fun ConnectionListScreen(
    logins: List<SavedLogin>,
    credentialStatus: (SavedLogin) -> CredentialStatus,
    onSelected: (SavedLogin) -> Unit,
    onForgetPassword: (SavedLogin) -> Unit,
    onDeleteLogin: (SavedLogin) -> Unit,
    onDismiss: () -> Unit,
) {
    var forgetTarget by remember { mutableStateOf<SavedLogin?>(null) }
    var deleteTarget by remember { mutableStateOf<SavedLogin?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.connections_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (logins.isEmpty()) {
                    Text(stringResource(R.string.connections_empty))
                }
                logins.forEach { login ->
                    Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text(login.displayName ?: login.serverUrl, style = MaterialTheme.typography.titleSmall)
                            if (login.displayName != null) {
                                Text(login.serverUrl, style = MaterialTheme.typography.bodySmall)
                            }
                            Text(login.identifier, style = MaterialTheme.typography.bodySmall)
                            Text(
                                listOfNotNull(
                                    credentialStatusLabel(credentialStatus(login)),
                                    formatTimestamp(login.lastUsedEpochMillis),
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Row {
                                TextButton(onClick = { onSelected(login) }) {
                                    Text(stringResource(R.string.connections_use))
                                }
                                TextButton(onClick = { forgetTarget = login }) {
                                    Text(stringResource(R.string.connections_forget_password))
                                }
                                TextButton(onClick = { deleteTarget = login }) {
                                    Text(stringResource(R.string.common_delete))
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_close)) } },
    )

    forgetTarget?.let { login ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_forget_title),
            message = stringResource(R.string.connections_forget_message, login.serverUrl, login.identifier),
            confirmLabel = stringResource(R.string.connections_forget_password),
            onConfirm = {
                forgetTarget = null
                onForgetPassword(login)
            },
            onDismiss = { forgetTarget = null },
        )
    }

    deleteTarget?.let { login ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_delete_title),
            message = stringResource(R.string.connections_delete_message, login.serverUrl, login.identifier),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                onDeleteLogin(login)
            },
            onDismiss = { deleteTarget = null },
        )
    }
}

/** Localised state of the saved password for one login. */
@Composable
internal fun credentialStatusLabel(status: CredentialStatus): String = stringResource(
    when (status) {
        CredentialStatus.None -> R.string.connections_no_password
        CredentialStatus.SavedByFingerprint -> R.string.connections_saved_password
        CredentialStatus.SavedByScreenLock -> R.string.connections_saved_password_screen_lock
        CredentialStatus.Unavailable -> R.string.connections_password_unavailable
        CredentialStatus.Invalidated -> R.string.connections_password_invalidated
    },
)
