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
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.theme.Spacing

/** Login-independent server-centre surface. All connection state and operations live in [ServerCenterViewModel]. */
@Composable
fun ServerCenterScreen(onClose: () -> Unit) {
    val viewModel: ServerCenterViewModel = viewModel()
    val state by viewModel.state.collectAsState()
    ServerCenterContent(
        state = state,
        onAddHostChanged = viewModel::updateAddHost,
        onAddPortChanged = viewModel::updateAddPort,
        onAddUserChanged = viewModel::updateAddUser,
        onAddNameChanged = viewModel::updateAddName,
        onAddPasswordChanged = viewModel::updateAddPassword,
        onAddAndVerify = viewModel::addAndVerify,
        onSelectHost = viewModel::selectHost,
        onManagePasswordChanged = viewModel::updateManagePassword,
        onVerifySelectedHost = viewModel::verifySelectedHost,
        onOpenSshFiles = viewModel::openSshFiles,
        onRequestDelete = viewModel::requestDelete,
        onClose = onClose,
    )

    val selected = state.hosts.firstOrNull { it.hostId == state.selectedHostId }
    if (state.deleteRequested && selected != null) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.server_center_remove_host),
            message = stringResource(R.string.server_center_remove_host_message, selected.displayName),
            confirmLabel = stringResource(R.string.server_center_remove_host),
            onConfirm = viewModel::removeSelectedHost,
            onDismiss = viewModel::dismissDelete,
        )
    }

    val trustRequest = state.verification as? ServerCenterSshVerification.NeedsTrust
    if (trustRequest != null) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.server_center_host_key_confirm_title),
            message = stringResource(R.string.server_center_host_key_review, trustRequest.observation.groupedFingerprint),
            confirmLabel = stringResource(R.string.server_center_trust_and_verify),
            onConfirm = viewModel::trustAndVerify,
            onDismiss = viewModel::dismissHostKeyTrust,
            busy = state.isVerifying,
        )
    }
}

@Composable
private fun ServerCenterContent(
    state: ServerCenterUiState,
    onAddHostChanged: (String) -> Unit,
    onAddPortChanged: (String) -> Unit,
    onAddUserChanged: (String) -> Unit,
    onAddNameChanged: (String) -> Unit,
    onAddPasswordChanged: (String) -> Unit,
    onAddAndVerify: () -> Unit,
    onSelectHost: (String) -> Unit,
    onManagePasswordChanged: (String) -> Unit,
    onVerifySelectedHost: () -> Unit,
    onOpenSshFiles: () -> Unit,
    onRequestDelete: () -> Unit,
    onClose: () -> Unit,
) {
    val selected = state.hosts.firstOrNull { it.hostId == state.selectedHostId }
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        Text(stringResource(R.string.server_center_title), style = MaterialTheme.typography.headlineSmall)
        Text(stringResource(R.string.server_center_subtitle), color = MaterialTheme.colorScheme.onSurfaceVariant)

        AddHostCard(
            state = state,
            onHostChanged = onAddHostChanged,
            onPortChanged = onAddPortChanged,
            onUserChanged = onAddUserChanged,
            onNameChanged = onAddNameChanged,
            onPasswordChanged = onAddPasswordChanged,
            onSubmit = onAddAndVerify,
        )
        ManagedHostList(hosts = state.hosts, onSelectHost = onSelectHost)
        selected?.let {
            HostManagementCard(
                target = it,
                state = state,
                onPasswordChanged = onManagePasswordChanged,
                onVerify = onVerifySelectedHost,
                onOpenSshFiles = onOpenSshFiles,
                onRequestDelete = onRequestDelete,
            )
        }
        OutlinedButton(onClose, Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_back)) }
    }
}

@Composable
private fun AddHostCard(
    state: ServerCenterUiState,
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onUserChanged: (String) -> Unit,
    onNameChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onSubmit: () -> Unit,
) {
    ElevatedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.server_center_add_host), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.server_center_add_host_hint),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodySmall,
            )
            OutlinedTextField(
                value = state.addHost,
                onValueChange = onHostChanged,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(stringResource(R.string.server_center_ssh_host)) },
                isError = state.inputError,
                singleLine = true,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                OutlinedTextField(
                    value = state.addPort,
                    onValueChange = onPortChanged,
                    modifier = Modifier.weight(1f),
                    label = { Text(stringResource(R.string.server_center_ssh_port)) },
                    isError = state.inputError,
                    singleLine = true,
                )
                OutlinedTextField(
                    value = state.addUser,
                    onValueChange = onUserChanged,
                    modifier = Modifier.weight(1f),
                    label = { Text(stringResource(R.string.server_center_ssh_user)) },
                    isError = state.inputError,
                    singleLine = true,
                )
            }
            PasswordTextField(
                value = state.addPassword,
                onValueChange = onPasswordChanged,
                label = stringResource(R.string.server_center_ssh_password),
                enabled = !state.isVerifying,
            )
            OutlinedTextField(
                value = state.addName,
                onValueChange = onNameChanged,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(stringResource(R.string.server_center_host_name_optional)) },
                singleLine = true,
            )
            if (state.inputError) Text(stringResource(R.string.server_center_invalid_host), color = MaterialTheme.colorScheme.error)
            Button(onClick = onSubmit, enabled = !state.isVerifying, modifier = Modifier.fillMaxWidth()) {
                ProgressOrText(state.isVerifying, R.string.server_center_add_and_verify)
            }
        }
    }
}

@Composable
private fun ManagedHostList(hosts: List<ServerHostTarget>, onSelectHost: (String) -> Unit) {
    if (hosts.isEmpty()) {
        Text(stringResource(R.string.server_center_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
        return
    }
    hosts.forEach { target ->
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(target.displayName, style = MaterialTheme.typography.titleMedium)
                Text(
                    stringResource(
                        R.string.server_center_host_summary,
                        target.sshUserName,
                        target.sshHost,
                        target.sshPort,
                        hostStatus(target),
                    ),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    style = MaterialTheme.typography.bodySmall,
                )
                TextButton(onClick = { onSelectHost(target.hostId) }) {
                    Text(stringResource(R.string.server_center_manage_host))
                }
            }
        }
    }
}

@Composable
private fun HostManagementCard(
    target: ServerHostTarget,
    state: ServerCenterUiState,
    onPasswordChanged: (String) -> Unit,
    onVerify: () -> Unit,
    onOpenSshFiles: () -> Unit,
    onRequestDelete: () -> Unit,
) {
    Text(stringResource(R.string.server_center_selected_host, target.displayName), style = MaterialTheme.typography.titleMedium)
    PasswordTextField(
        value = state.managePassword,
        onValueChange = onPasswordChanged,
        label = stringResource(R.string.server_center_ssh_password),
        enabled = !state.isVerifying,
    )
    Button(
        onClick = onVerify,
        enabled = state.managePassword.isNotEmpty() && !state.isVerifying,
        modifier = Modifier.fillMaxWidth(),
    ) {
        ProgressOrText(state.isVerifying, R.string.server_center_verify_ssh)
    }
    VerificationNotice(state.verification)
    if (state.verification is ServerCenterSshVerification.Trusted) {
        OutlinedButton(onClick = onOpenSshFiles, modifier = Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.ssh_files_open))
        }
    }
    TextButton(onClick = onRequestDelete) {
        Text(stringResource(R.string.server_center_remove_host), color = MaterialTheme.colorScheme.error)
    }
}

@Composable
private fun VerificationNotice(
    verification: ServerCenterSshVerification?,
) {
    when (verification) {
        is ServerCenterSshVerification.Trusted -> Text(
            stringResource(R.string.server_center_ssh_verified, verification.fingerprint.orEmpty()),
            color = MaterialTheme.colorScheme.primary,
        )
        is ServerCenterSshVerification.NeedsTrust -> Unit
        is ServerCenterSshVerification.KeyChanged -> Text(
            stringResource(R.string.server_center_host_key_changed, verification.observation.groupedFingerprint),
            color = MaterialTheme.colorScheme.error,
        )
        ServerCenterSshVerification.Failed -> Text(
            stringResource(R.string.server_center_ssh_failed),
            color = MaterialTheme.colorScheme.error,
        )
        null -> Unit
    }
}

@Composable
private fun ProgressOrText(isWorking: Boolean, textRes: Int) {
    if (isWorking) CircularProgressIndicator() else Text(stringResource(textRes))
}

@Composable
private fun hostStatus(target: ServerHostTarget): String = when {
    target.lastVerified == null -> stringResource(R.string.server_center_status_unverified)
    target.lastVerified.installed && target.lastVerified.healthy -> stringResource(R.string.server_center_status_healthy_cached)
    target.lastVerified.installed -> stringResource(R.string.server_center_status_unhealthy_cached)
    else -> stringResource(R.string.server_center_status_not_installed_cached)
}
