package app.relaxkonos.mobile.ui.connect

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.icons.ServerPlatformBadge
import app.relaxkonos.mobile.ui.icons.hostPlatformMark
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * The saved-login picker shown from the sign-in screen.
 *
 * Each row offers two actions that never stand in for each other
 * (`LoginCredentials.Design.md` §6.3): **forget the password** drops the credential
 * and keeps the login, **delete login** drops both. Both address exactly one row — nothing here acts on
 * a server address alone, because one server can hold several accounts and their records are independent.
 *
 * Whether a password is saved is asked of the vault rather than read from the profile list, so the list
 * cannot claim a credential the user already removed (§4.2).
 *
 * The rows are scrollable: the number of saved logins is the user's business, and a dialog that clips
 * its own content would hide the very action the list exists for.
 *
 * Each row carries its host's operating system mark once the server has answered what it runs on
 * (§6.3). That answer is fetched when the list is opened (`HostOperatingSystemLookup`), so a row whose
 * answer arrives late simply re-renders with its mark instead of holding up the dialog.
 */
@Composable
fun ConnectionListScreen(
    logins: List<SavedLogin>,
    ownerDeviceServiceIds: List<String>,
    credentialStatus: (SavedLogin) -> CredentialStatus,
    onSelected: (SavedLogin) -> Unit,
    onOwnerDeviceSelected: (String) -> Unit,
    onForgetPassword: (SavedLogin) -> Unit,
    onDeleteLogin: (SavedLogin) -> Unit,
    onDismiss: () -> Unit,
) {
    var forgetTarget by remember { mutableStateOf<SavedLogin?>(null) }
    var deleteTarget by remember { mutableStateOf<SavedLogin?>(null) }
    var actionTarget by remember { mutableStateOf<SavedLogin?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.connections_title)) },
        text = {
            if (logins.isEmpty() && ownerDeviceServiceIds.isEmpty()) {
                EmptyState(text = stringResource(R.string.connections_empty))
            } else {
                Column(
                    modifier = Modifier.verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    PlatformTrademarkLegend()
                    ownerDeviceServiceIds.forEach { serviceId ->
                        OwnerDeviceEntry(serviceId = serviceId, onSelect = { onOwnerDeviceSelected(serviceId) })
                    }
                    logins.forEach { login ->
                        SwipeableSavedLoginEntry(
                            login = login,
                            statusLabel = credentialStatusLabel(credentialStatus(login)),
                            onSelect = { onSelected(login) },
                            onManage = { actionTarget = login },
                        )
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_close)) } },
    )

    actionTarget?.let { login ->
        AlertDialog(
            onDismissRequest = { actionTarget = null },
            title = { Text(login.displayName ?: login.serviceId) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(stringResource(R.string.connections_swipe_actions))
                    OutlinedButton(
                        onClick = { actionTarget = null; forgetTarget = login },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.connections_forget_password)) }
                    Button(
                        onClick = { actionTarget = null; deleteTarget = login },
                        modifier = Modifier.fillMaxWidth(),
                        colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                    ) { ActionLabel(R.string.common_delete) }
                }
            },
            confirmButton = { TextButton(onClick = { actionTarget = null }) { Text(stringResource(R.string.common_close)) } },
        )
    }

    forgetTarget?.let { login ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_forget_title),
            message = stringResource(R.string.connections_forget_message, login.serviceId, login.identifier),
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
            message = stringResource(R.string.connections_delete_message, login.serviceId, login.identifier),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                onDeleteLogin(login)
            },
            onDismiss = { deleteTarget = null },
        )
    }
}

@Composable
private fun OwnerDeviceEntry(serviceId: String, onSelect: () -> Unit) {
    ListRow(
        title = stringResource(R.string.connections_windows_device),
        subtitle = serviceId,
        supporting = stringResource(R.string.connections_windows_device_support),
        leading = { ServerPlatformBadge(serviceId) },
        onClick = onSelect,
    )
}

/** Small legend makes the operating-system marks unambiguous without claiming a server version we do not know. */
@Composable
private fun PlatformTrademarkLegend() {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
            // Driven by the same mapping the rows use, so the legend cannot end up advertising a mark
            // that no row can ever show (or miss one that a row can).
            HostOperatingSystemKind.entries
                .filter { it != HostOperatingSystemKind.Unknown }
                .forEach { IconBadge(icon = hostPlatformMark(it)) }
        }
        Text(
            stringResource(R.string.connections_platform_trademarks),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** A left swipe exposes the destructive management path without competing with the connect tap. */
@OptIn(ExperimentalMaterial3Api::class)
@Suppress("DEPRECATION") // The current Material state API needs this callback to keep the row open.
@Composable
private fun SwipeableSavedLoginEntry(
    login: SavedLogin,
    statusLabel: String,
    onSelect: () -> Unit,
    onManage: () -> Unit,
) {
    val dismissState = rememberSwipeToDismissBoxState(
        confirmValueChange = { target ->
            if (target == SwipeToDismissBoxValue.EndToStart) onManage()
            false
        },
    )
    SwipeToDismissBox(
        state = dismissState,
        enableDismissFromStartToEnd = false,
        backgroundContent = {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.errorContainer, RoundedCornerShape(Radius.md))
                    .padding(horizontal = Spacing.md, vertical = Spacing.sm),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    stringResource(R.string.connections_forget_password),
                    color = MaterialTheme.colorScheme.onErrorContainer,
                    style = MaterialTheme.typography.labelMedium,
                )
                Spacer(Modifier.width(Spacing.md))
                Text(
                    stringResource(R.string.common_delete),
                    color = MaterialTheme.colorScheme.onErrorContainer,
                    style = MaterialTheme.typography.labelMedium,
                )
            }
        },
    ) {
        Surface(shape = RoundedCornerShape(Radius.md), color = MaterialTheme.colorScheme.surface) {
            ListRow(
                title = login.displayName ?: login.serviceId,
                subtitle = login.displayName?.let { login.serviceId },
                supporting = listOf(
                    stringResource(
                        if (login.directServerUrl == null) R.string.connections_managed_server
                        else R.string.connections_direct_server,
                    ),
                    login.identifier,
                    statusLabel,
                    formatTimestamp(login.lastUsedEpochMillis),
                ).joinToString(" · "),
                // The host's own operating system once the server has said what it is, the generic
                // connection mark while it has not: a row never guesses a platform from an address
                // (`LoginCredentials.Design.md` §6.3).
                leading = {
                    ServerPlatformBadge(login.serviceId)
                },
                trailing = {
                    Text(
                        stringResource(R.string.connections_connect),
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.labelLarge,
                        textAlign = TextAlign.End,
                    )
                },
                onClick = onSelect,
            )
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
        CredentialStatus.SavedInDebugBuild -> R.string.connections_saved_password_debug
        CredentialStatus.Unavailable -> R.string.connections_password_unavailable
        CredentialStatus.Invalidated -> R.string.connections_password_invalidated
    },
)
