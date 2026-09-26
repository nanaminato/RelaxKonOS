package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerCenterCoordinator
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerHostTargetRules
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

/** First, login-independent server-centre surface. SSH authentication and operations are added from host detail. */
@Composable
fun ServerCenterScreen(coordinator: ServerCenterCoordinator, onClose: () -> Unit) {
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("22") }
    var user by remember { mutableStateOf("") }
    var name by remember { mutableStateOf("") }
    var inputError by remember { mutableStateOf(false) }
    var selected by remember { mutableStateOf<ServerHostTarget?>(null) }
    var sshPassword by remember { mutableStateOf("") }
    var verification by remember { mutableStateOf<ServerCenterSshVerification?>(null) }
    var deleteRequested by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val hosts = coordinator.hosts()

    Column(
        Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        Text(stringResource(R.string.server_center_title), style = MaterialTheme.typography.headlineSmall)
        Text(stringResource(R.string.server_center_subtitle), color = MaterialTheme.colorScheme.onSurfaceVariant)

        if (hosts.isEmpty()) {
            Text(stringResource(R.string.server_center_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
        } else {
            hosts.forEach { target ->
                val status = when {
                    target.lastVerified == null -> stringResource(R.string.server_center_status_unverified)
                    target.lastVerified.installed && target.lastVerified.healthy -> stringResource(R.string.server_center_status_healthy_cached)
                    target.lastVerified.installed -> stringResource(R.string.server_center_status_unhealthy_cached)
                    else -> stringResource(R.string.server_center_status_not_installed_cached)
                }
                Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    Text(target.displayName, style = MaterialTheme.typography.titleMedium)
                    Text(
                        stringResource(R.string.server_center_host_summary, target.sshUserName, target.sshHost, target.sshPort, status),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.bodySmall,
                    )
                    TextButton(onClick = { selected = target; verification = null }) {
                        Text(stringResource(R.string.server_center_manage_host))
                    }
                }
            }
        }

        selected?.let { target ->
            Text(stringResource(R.string.server_center_selected_host, target.displayName), style = MaterialTheme.typography.titleMedium)
            PasswordTextField(
                value = sshPassword,
                onValueChange = { sshPassword = it; verification = null },
                label = stringResource(R.string.server_center_ssh_password),
            )
            Button(
                onClick = {
                    val secret = sshPassword.toCharArray()
                    scope.launch {
                        verification = coordinator.verifySsh(
                            target.hostId,
                            SshCredential(SshCredentialKind.Password, secret, null),
                        )
                    }
                },
                enabled = sshPassword.isNotEmpty(),
                modifier = Modifier.fillMaxWidth(),
            ) { Text(stringResource(R.string.server_center_verify_ssh)) }
            when (val result = verification) {
                is ServerCenterSshVerification.Trusted -> Text(
                    stringResource(R.string.server_center_ssh_verified, result.fingerprint.orEmpty()),
                    color = MaterialTheme.colorScheme.primary,
                )
                is ServerCenterSshVerification.NeedsTrust -> {
                    Text(stringResource(R.string.server_center_host_key_review, result.observation.groupedFingerprint))
                    Button(
                        onClick = { coordinator.trustHostKey(target, result.observation); verification = null },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.server_center_trust_host_key)) }
                }
                is ServerCenterSshVerification.KeyChanged -> Text(
                    stringResource(R.string.server_center_host_key_changed, result.observation.groupedFingerprint),
                    color = MaterialTheme.colorScheme.error,
                )
                ServerCenterSshVerification.Failed -> Text(
                    stringResource(R.string.server_center_ssh_failed),
                    color = MaterialTheme.colorScheme.error,
                )
                null -> Unit
            }
            TextButton(onClick = { deleteRequested = true }) {
                Text(stringResource(R.string.server_center_remove_host), color = MaterialTheme.colorScheme.error)
            }
        }

        Text(stringResource(R.string.server_center_add_host), style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(host, { host = it; inputError = false }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_center_ssh_host)) }, isError = inputError, singleLine = true)
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
            OutlinedTextField(port, { port = it; inputError = false }, Modifier.weight(1f), label = { Text(stringResource(R.string.server_center_ssh_port)) }, isError = inputError, singleLine = true)
            OutlinedTextField(user, { user = it; inputError = false }, Modifier.weight(1f), label = { Text(stringResource(R.string.server_center_ssh_user)) }, isError = inputError, singleLine = true)
        }
        OutlinedTextField(name, { name = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_center_host_name_optional)) }, singleLine = true)
        if (inputError) Text(stringResource(R.string.server_center_invalid_host), color = MaterialTheme.colorScheme.error)
        Button(
            onClick = {
                val number = port.toIntOrNull()
                if (number == null || !ServerHostTargetRules.isValidEndpoint(host, number, user)) inputError = true
                else {
                    coordinator.addHost(host, number, user, name.ifBlank { null })
                    host = ""; port = "22"; user = ""; name = ""
                }
            },
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.server_center_add)) }
        OutlinedButton(onClose, Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_back)) }
    }

    if (deleteRequested) {
        val target = selected
        if (target != null) {
            ConfirmDangerousDialog(
                title = stringResource(R.string.server_center_remove_host),
                message = stringResource(R.string.server_center_remove_host_message, target.displayName),
                confirmLabel = stringResource(R.string.server_center_remove_host),
                onConfirm = {
                    coordinator.removeHost(target.hostId)
                    selected = null
                    sshPassword = ""
                    verification = null
                    deleteRequested = false
                },
                onDismiss = { deleteRequested = false },
            )
        } else {
            deleteRequested = false
        }
    }
}
