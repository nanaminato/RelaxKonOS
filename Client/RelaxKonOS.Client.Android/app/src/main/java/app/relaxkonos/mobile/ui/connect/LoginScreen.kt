package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.core.auth.LoginDecision
import app.relaxkonos.mobile.core.auth.credentialState
import app.relaxkonos.mobile.core.auth.credentialStatus
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.text

/**
 * Sign-in screen, in one shape.
 *
 * The address, the account and the password are always visible. There is deliberately no "short form"
 * that hides the password field when a credential is stored: hiding it encodes "a password is saved" as
 * "a field is missing", which is not a state anything can read or act on
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §6.1, D7).
 *
 * The password field only ever holds what the user typed in this session. That a password is saved is
 * reported by the line beside the field, which is derived from the vault and never from the field's
 * contents (§3, §6.2).
 */
@Composable
fun LoginScreen(modifier: Modifier = Modifier) {
    val activity = LocalContext.current as? FragmentActivity ?: return
    val viewModel: LoginViewModel = viewModel()
    val container = appContainer()

    val serverFocus = remember { FocusRequester() }
    val identifierFocus = remember { FocusRequester() }
    val passwordFocus = remember { FocusRequester() }

    // A click that cannot proceed moves the caret to the field it is waiting for, instead of only
    // showing a sentence about it.
    LaunchedEffect(viewModel.focusRequest) {
        val target = when (viewModel.focusRequest) {
            LoginField.Server -> serverFocus
            LoginField.Identifier -> identifierFocus
            LoginField.Password -> passwordFocus
            null -> null
        }
        if (target != null) {
            runCatching { target.requestFocus() }
            viewModel.consumeFocusRequest()
        }
    }

    val unlockMode = viewModel.vaultUnlockMode
    val actionLabel = when {
        viewModel.isLoggingIn -> R.string.login_action_connecting
        viewModel.decision == LoginDecision.UnlockSavedCredential -> R.string.login_action_sign_in
        else -> R.string.login_action_connect
    }

    Column(
        modifier = modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Card(Modifier.fillMaxWidth().widthIn(max = 520.dp)) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(stringResource(R.string.login_title), style = MaterialTheme.typography.headlineSmall)
                Text(
                    stringResource(R.string.login_subtitle),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    style = MaterialTheme.typography.bodyMedium,
                )

                viewModel.message?.let { banner ->
                    ErrorBanner(
                        message = banner.text(),
                        onRetry = null,
                        onDismiss = { viewModel.dismissMessage() },
                    )
                }

                OutlinedTextField(
                    value = viewModel.serverUrl,
                    onValueChange = { viewModel.changeServer(it) },
                    modifier = Modifier.fillMaxWidth().focusRequester(serverFocus),
                    label = { Text(stringResource(R.string.login_server_address)) },
                    singleLine = true,
                    enabled = !viewModel.isLoggingIn,
                )
                OutlinedTextField(
                    value = viewModel.identifier,
                    onValueChange = { viewModel.changeIdentifier(it) },
                    modifier = Modifier.fillMaxWidth().focusRequester(identifierFocus),
                    label = { Text(stringResource(R.string.login_identifier)) },
                    singleLine = true,
                    enabled = !viewModel.isLoggingIn,
                )
                PasswordTextField(
                    value = viewModel.passwordText,
                    onValueChange = { viewModel.changePassword(it) },
                    label = stringResource(R.string.login_password),
                    modifier = Modifier.focusRequester(passwordFocus),
                    enabled = !viewModel.isLoggingIn,
                    supportingText = credentialStatusLine(viewModel.credentialStatus),
                )

                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = viewModel.rememberCredential,
                        onCheckedChange = { viewModel.rememberCredential = it },
                        enabled = !viewModel.isLoggingIn && unlockMode != null,
                    )
                    Text(stringResource(R.string.login_remember_hint))
                }
                if (unlockMode == null) {
                    Text(
                        stringResource(R.string.login_no_fingerprint),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else if (unlockMode == VaultUnlockMode.DeviceUnlockWindow) {
                    Text(
                        stringResource(R.string.vault_device_window_notice),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                Button(
                    onClick = { viewModel.submit(activity) },
                    enabled = !viewModel.isLoggingIn,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text(stringResource(actionLabel)) }

                if (viewModel.hasLogins) {
                    OutlinedButton(onClick = { viewModel.openConnections() }, modifier = Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.connections_title))
                    }
                }
            }
        }
    }

    if (viewModel.connectionsOpen) {
        ConnectionListScreen(
            logins = viewModel.logins,
            credentialStatus = { login ->
                val mode = container.unlockMode(VaultKind.Connection)
                credentialStatus(
                    credentialState(container.vault.record(VaultKind.Connection, login.serverUrl, login.identifier), mode),
                    mode,
                )
            },
            onSelected = { viewModel.select(it) },
            onForgetPassword = { viewModel.forgetPassword(it) },
            onDeleteLogin = { viewModel.deleteLogin(it) },
            onDismiss = { viewModel.closeConnections() },
        )
    }
}

/** The saved-password line beside the field, or nothing when none is stored. */
@Composable
private fun credentialStatusLine(status: CredentialStatus): (@Composable () -> Unit)? {
    val label = when (status) {
        CredentialStatus.None -> return null
        CredentialStatus.SavedByFingerprint -> stringResource(R.string.login_saved_password_fingerprint)
        CredentialStatus.SavedByScreenLock -> stringResource(R.string.login_saved_password_screen_lock)
        CredentialStatus.Unavailable -> stringResource(R.string.login_saved_password_unavailable)
        CredentialStatus.Invalidated -> stringResource(R.string.login_saved_password_invalidated)
    }
    val color: Color = when (status) {
        CredentialStatus.Unavailable, CredentialStatus.Invalidated -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    return {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Icon(
                imageVector = Icons.Filled.Lock,
                // The sentence already says what this means; the icon is decoration for it.
                contentDescription = null,
                modifier = Modifier.size(16.dp),
                tint = color,
            )
            Text(label, style = MaterialTheme.typography.bodySmall, color = color)
        }
    }
}
