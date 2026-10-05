package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.SectionLabel
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.connect.credentialStatusLabel
import app.relaxkonos.mobile.ui.icons.ServerPlatformBadge
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Connections, inside the shell.
 *
 * The same content as the sign-in list, with one difference that only makes sense while signed in:
 * the actions also affect the stored credential. They stay two separate actions, per row
 * (`LoginCredentials.Design.md` §6.3): forgetting a password keeps the login, deleting
 * a login removes both.
 *
 * Switching explicitly ends the current session and continues through the ordinary sign-in flow.
 */
@Composable
fun ConnectionsScreen(
    onBack: (() -> Unit)?,
    onSwitchLogin: (SavedLogin) -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val controller = remember(container) { ConnectionsController(container) }
    var revision by remember { mutableStateOf(0) }
    var forgetTarget by remember { mutableStateOf<SavedLogin?>(null) }
    var deleteTarget by remember { mutableStateOf<SavedLogin?>(null) }

    val logins = remember(revision) { controller.logins }
    val active = container.activeSession
    LaunchedEffect(controller) { controller.resolvePlatforms() }

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
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        ServerPlatformBadge(login.serviceId)
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
                                credentialStatusLabel(controller.status(login)),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            if (login.serviceId == active?.serviceId && login.identifier == active.userName) {
                                Text(
                                    stringResource(R.string.connections_active),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.primary,
                                )
                            }
                        }
                        Column {
                            TextButton(onClick = { onSwitchLogin(login) }) {
                                Text(stringResource(R.string.more_switch_login))
                            }
                            TextButton(onClick = { forgetTarget = login }) {
                                Text(stringResource(R.string.connections_forget_password))
                            }
                            TextButton(onClick = { deleteTarget = login }) {
                                ActionLabel(R.string.common_delete)
                            }
                        }
                    }
                }
            }
        }

        // Host lifecycle is intentionally adjacent to saved logins, but remains a separate record
        // type: removing a login above never removes this device's SSH host information.
        SectionLabel(stringResource(R.string.server_center_title))
        SectionGroup {
            Text(
                stringResource(R.string.server_center_connections_hint),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodyMedium,
            )
            OutlinedButton(onClick = container.serverCenter::open, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.server_center_open))
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
                controller.forgetPassword(login)
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
                controller.delete(login)
                revision++
            },
            onDismiss = { deleteTarget = null },
        )
    }
}
