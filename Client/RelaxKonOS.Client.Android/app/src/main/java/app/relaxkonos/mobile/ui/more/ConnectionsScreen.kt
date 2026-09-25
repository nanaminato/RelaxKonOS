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
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.credentialState
import app.relaxkonos.mobile.core.auth.credentialStatus
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.connect.credentialStatusLabel
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Connections, inside the shell.
 *
 * The same content as the sign-in list, with one difference that only makes sense while signed in:
 * the actions also affect the stored credential. They stay two separate actions, per row
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §6.3): forgetting a password keeps the login, deleting
 * a login removes both.
 *
 * Switching to another server is not offered here — that would mean tearing down the live session, and
 * the design keeps sign-out as the single, explicit way to do that.
 */
@Composable
fun ConnectionsScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    var revision by remember { mutableStateOf(0) }
    var forgetTarget by remember { mutableStateOf<SavedLogin?>(null) }
    var deleteTarget by remember { mutableStateOf<SavedLogin?>(null) }

    val logins = remember(revision) { container.profiles.all() }
    val activeServiceId = container.session.serviceId

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.connections_title),
            onBack = onBack,
        )

        // The page header already names this list, so the group carries no title of its own.
        SectionGroup {
            if (logins.isEmpty()) {
                EmptyHint(stringResource(R.string.connections_empty))
            } else {
                logins.forEach { login ->
                    val mode = container.unlockMode(VaultKind.Connection)
                    val record = container.vault.record(VaultKind.Connection, login.serviceId, login.identifier)
                    Row(Modifier.fillMaxWidth()) {
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                            Text(login.displayName ?: login.serviceId, style = MaterialTheme.typography.bodyLarge)
                            if (login.displayName != null) {
                                Text(login.serviceId, style = MaterialTheme.typography.bodySmall)
                            }
                            Text(
                                listOfNotNull(login.identifier, formatTimestamp(login.lastUsedEpochMillis)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(
                                credentialStatusLabel(credentialStatus(credentialState(record, mode), mode)),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            if (login.serviceId == activeServiceId) {
                                Text(
                                    stringResource(R.string.connections_active),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.primary,
                                )
                            }
                        }
                        Column {
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

        Text(
            stringResource(R.string.connections_sign_out_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }

    forgetTarget?.let { login ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_forget_title),
            message = stringResource(R.string.connections_forget_message, login.serviceId, login.identifier),
            confirmLabel = stringResource(R.string.connections_forget_password),
            onConfirm = {
                forgetTarget = null
                container.vault.delete(VaultKind.Connection, login.serviceId, login.identifier)
                container.forgetDebugCredential(login.serviceId, login.identifier)
                container.profiles.setHasSavedCredential(login.serviceId, login.identifier, false)
                revision++
            },
            onDismiss = { forgetTarget = null },
        )
    }

    deleteTarget?.let { login ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_delete_title),
            message = stringResource(R.string.connections_delete_message, login.serviceId, login.identifier),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                container.vault.delete(VaultKind.Connection, login.serviceId, login.identifier)
                container.forgetDebugCredential(login.serviceId, login.identifier)
                // One login, not the whole server: another account on it keeps its own record (§6.3).
                container.profiles.remove(login.serviceId, login.identifier)
                revision++
            },
            onDismiss = { deleteTarget = null },
        )
    }
}
