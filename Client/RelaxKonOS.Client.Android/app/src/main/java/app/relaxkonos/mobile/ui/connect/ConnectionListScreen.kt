package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.theme.Spacing

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
 *
 * The rows are scrollable: the number of saved logins is the user's business, and a dialog that clips
 * its own content would hide the very action the list exists for.
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
            if (logins.isEmpty()) {
                EmptyState(text = stringResource(R.string.connections_empty))
            } else {
                Column(
                    modifier = Modifier.verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    logins.forEach { login ->
                        SavedLoginEntry(
                            login = login,
                            statusLabel = credentialStatusLabel(credentialStatus(login)),
                            onSelect = { onSelected(login) },
                            onForget = { forgetTarget = login },
                            onDelete = { deleteTarget = login },
                        )
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

/**
 * One saved login.
 *
 * The identifier, the credential state and the last use share one line because they are all detail;
 * the actions sit under them, where they read as belonging to this entry and not to the one above.
 */
@Composable
private fun SavedLoginEntry(
    login: SavedLogin,
    statusLabel: String,
    onSelect: () -> Unit,
    onForget: () -> Unit,
    onDelete: () -> Unit,
) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        ListRow(
            title = login.displayName ?: login.serverUrl,
            subtitle = login.displayName?.let { login.serverUrl },
            supporting = listOfNotNull(
                login.identifier,
                statusLabel,
                formatTimestamp(login.lastUsedEpochMillis),
            ).joinToString(" · "),
            leading = { IconBadge(icon = painterResource(R.drawable.ic_link)) },
        )
        Row(Modifier.fillMaxWidth().padding(start = Spacing.sm), horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            TextButton(onClick = onSelect) { Text(stringResource(R.string.connections_use)) }
            TextButton(onClick = onForget) { Text(stringResource(R.string.connections_forget_password)) }
            TextButton(onClick = onDelete) { Text(stringResource(R.string.common_delete)) }
        }
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
