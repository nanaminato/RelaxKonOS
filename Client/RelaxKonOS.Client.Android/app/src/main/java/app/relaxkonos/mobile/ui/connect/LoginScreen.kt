package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
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
import androidx.compose.ui.focus.onFocusChanged
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
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

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
 *
 * The brand mark sits above the card rather than inside it, so the card stays a form: one opaque
 * surface holding exactly the three fields and the action, with nothing decorative competing with them.
 */
@Composable
fun LoginScreen(modifier: Modifier = Modifier) {
    val activity = LocalContext.current as? FragmentActivity ?: return
    val viewModel: LoginViewModel = viewModel()

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
        modifier = modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().widthIn(max = 520.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            LoginBrand()

            Spacer(Modifier.height(Spacing.xl))

            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(Radius.xl),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.55f)),
            ) {
                Column(Modifier.padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    Text(stringResource(R.string.login_title), style = MaterialTheme.typography.titleLarge)
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
                        modifier = Modifier.fillMaxWidth().focusRequester(serverFocus).onFocusChanged {
                            if (!it.isFocused) viewModel.discoverServerEndpoint()
                        },
                        label = { Text(stringResource(R.string.login_server_address)) },
                        supportingText = { endpointDiscoveryStatus(viewModel.endpointDiscoveryState) },
                        singleLine = true,
                        enabled = !viewModel.isLoggingIn,
                        shape = MaterialTheme.shapes.medium,
                    )
                    OutlinedTextField(
                        value = viewModel.identifier,
                        onValueChange = { viewModel.changeIdentifier(it) },
                        modifier = Modifier.fillMaxWidth().focusRequester(identifierFocus),
                        label = { Text(stringResource(R.string.login_identifier)) },
                        singleLine = true,
                        enabled = !viewModel.isLoggingIn && viewModel.endpointDiscoveryState != EndpointDiscoveryState.Checking,
                        shape = MaterialTheme.shapes.medium,
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
                            // The debug plaintext store makes saving possible where the vault cannot be
                            // created at all, so the switch has to follow the same condition the store
                            // itself follows — never a looser one.
                            enabled = !viewModel.isLoggingIn && (unlockMode != null || viewModel.debugFallbackAvailable),
                        )
                        Text(
                            stringResource(
                                if (viewModel.debugFallbackAvailable) {
                                    R.string.login_remember_hint_debug
                                } else {
                                    R.string.login_remember_hint
                                },
                            ),
                        )
                    }
                    when {
                        viewModel.debugFallbackAvailable -> Text(
                            stringResource(R.string.login_no_lock_screen_debug),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error,
                        )

                        unlockMode == null -> Text(
                            stringResource(R.string.login_no_fingerprint),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )

                        unlockMode == VaultUnlockMode.DeviceUnlockWindow -> Text(
                            stringResource(R.string.vault_device_window_notice),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    Button(
                        onClick = { viewModel.submit(activity) },
                        enabled = !viewModel.isLoggingIn && viewModel.endpointDiscoveryState != EndpointDiscoveryState.Checking,
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
    }

    if (viewModel.connectionsOpen) {
        ConnectionListScreen(
            logins = viewModel.logins,
            // The row asks the same question the form does, through the same function, so a login
            // cannot be listed as having no password while the field above says one is saved.
            credentialStatus = { login -> viewModel.savedCredentialStatus(login.serviceId, login.identifier) },
            onSelected = { viewModel.select(it) },
            onForgetPassword = { viewModel.forgetPassword(it) },
            onDeleteLogin = { viewModel.deleteLogin(it) },
            onDismiss = { viewModel.closeConnections() },
        )
    }
}

/**
 * The wordmark above the form.
 *
 * It is the desktop client's own mark, drawn at the size the gradient plate used to occupy, which is
 * how the two ends of the app (signed out, signed in) read as one product. The artwork brings its own
 * colour and shape, so the plate it used to sit on is gone.
 */
@Composable
private fun LoginBrand() {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        DesktopIcon(icon = DesktopIcons.brand, size = 72.dp)
        Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineSmall)
    }
}

/** The saved-password line beside the field, or nothing when none is stored. */
@Composable
private fun credentialStatusLine(status: CredentialStatus): (@Composable () -> Unit)? {
    val label = when (status) {
        CredentialStatus.None -> return null
        CredentialStatus.SavedByFingerprint -> stringResource(R.string.login_saved_password_fingerprint)
        CredentialStatus.SavedByScreenLock -> stringResource(R.string.login_saved_password_screen_lock)
        CredentialStatus.SavedInDebugBuild -> stringResource(R.string.login_saved_password_debug)
        CredentialStatus.Unavailable -> stringResource(R.string.login_saved_password_unavailable)
        CredentialStatus.Invalidated -> stringResource(R.string.login_saved_password_invalidated)
    }
    val color: Color = when (status) {
        CredentialStatus.Unavailable, CredentialStatus.Invalidated -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    return {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Spacing.xs + 2.dp)) {
            DesktopIcon(icon = DesktopIcons.credentials, size = 16.dp)
            Text(label, style = MaterialTheme.typography.bodySmall, color = color)
        }
    }
}

@Composable
private fun endpointDiscoveryStatus(state: EndpointDiscoveryState) {
    val label = when (state) {
        EndpointDiscoveryState.Idle -> return
        EndpointDiscoveryState.Checking -> R.string.login_server_checking
        EndpointDiscoveryState.Found -> R.string.login_server_found
        EndpointDiscoveryState.InvalidAddress -> R.string.login_server_invalid
        EndpointDiscoveryState.Unavailable -> R.string.login_server_unavailable
    }
    val color = when (state) {
        EndpointDiscoveryState.InvalidAddress, EndpointDiscoveryState.Unavailable -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    Text(stringResource(label), style = MaterialTheme.typography.bodySmall, color = color)
}
