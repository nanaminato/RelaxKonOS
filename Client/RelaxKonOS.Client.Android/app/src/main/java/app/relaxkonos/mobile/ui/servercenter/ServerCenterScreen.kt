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

@Composable
fun ServerCenterScreen(onClose: () -> Unit) {
    val viewModel: ServerCenterViewModel = viewModel()
    val state by viewModel.state.collectAsState()
    ServerCenterContent(
        state = state,
        onHostChanged = viewModel::updateHost,
        onPortChanged = viewModel::updatePort,
        onUserChanged = viewModel::updateUser,
        onNameChanged = viewModel::updateName,
        onPasswordChanged = viewModel::updatePassword,
        onAddAndVerify = viewModel::addAndVerify,
        onVerifyAndOpen = viewModel::verifyAndOpen,
        onManage = viewModel::manage,
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
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onUserChanged: (String) -> Unit,
    onNameChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onAddAndVerify: () -> Unit,
    onVerifyAndOpen: () -> Unit,
    onManage: (String) -> Unit,
    onRequestDelete: () -> Unit,
    onClose: () -> Unit,
) {
    val managing = state.formMode == ServerCenterFormMode.Manage
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        Text(stringResource(R.string.server_center_title), style = MaterialTheme.typography.headlineSmall)
        Text(stringResource(R.string.server_center_subtitle), color = MaterialTheme.colorScheme.onSurfaceVariant)
        HostForm(
            state = state,
            managing = managing,
            onHostChanged = onHostChanged,
            onPortChanged = onPortChanged,
            onUserChanged = onUserChanged,
            onNameChanged = onNameChanged,
            onPasswordChanged = onPasswordChanged,
            onSubmit = if (managing) onVerifyAndOpen else onAddAndVerify,
        )
        VerificationNotice(state.verification)
        if (managing) {
            TextButton(onClick = onRequestDelete) {
                Text(stringResource(R.string.server_center_remove_host), color = MaterialTheme.colorScheme.error)
            }
        }
        ManagedHostList(state.hosts, onManage)
        OutlinedButton(onClose, Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_back)) }
    }
}

@Composable
private fun HostForm(
    state: ServerCenterUiState,
    managing: Boolean,
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onUserChanged: (String) -> Unit,
    onNameChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onSubmit: () -> Unit,
) {
    ElevatedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(
                stringResource(if (managing) R.string.server_center_manage_host else R.string.server_center_add_host),
                style = MaterialTheme.typography.titleMedium,
            )
            if (!managing) {
                Text(stringResource(R.string.server_center_add_host_hint), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
            }
            OutlinedTextField(value = state.host, onValueChange = onHostChanged, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_center_ssh_host)) }, isError = state.inputError, singleLine = true, enabled = !managing && !state.isVerifying)
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                OutlinedTextField(value = state.port, onValueChange = onPortChanged, modifier = Modifier.weight(1f), label = { Text(stringResource(R.string.server_center_ssh_port)) }, isError = state.inputError, singleLine = true, enabled = !managing && !state.isVerifying)
                OutlinedTextField(value = state.user, onValueChange = onUserChanged, modifier = Modifier.weight(1f), label = { Text(stringResource(R.string.server_center_ssh_user)) }, isError = state.inputError, singleLine = true, enabled = !managing && !state.isVerifying)
            }
            if (!managing) {
                OutlinedTextField(value = state.name, onValueChange = onNameChanged, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_center_host_name_optional)) }, singleLine = true, enabled = !state.isVerifying)
            }
            PasswordTextField(state.password, onPasswordChanged, stringResource(R.string.server_center_ssh_password), enabled = !state.isVerifying)
            if (state.inputError) Text(stringResource(R.string.server_center_invalid_host), color = MaterialTheme.colorScheme.error)
            Button(onSubmit, enabled = state.password.isNotEmpty() && !state.isVerifying, modifier = Modifier.fillMaxWidth()) {
                if (state.isVerifying) CircularProgressIndicator() else Text(stringResource(if (managing) R.string.server_center_verify_and_open else R.string.server_center_add_and_verify))
            }
        }
    }
}

@Composable
private fun ManagedHostList(hosts: List<ServerHostTarget>, onManage: (String) -> Unit) {
    if (hosts.isEmpty()) {
        Text(stringResource(R.string.server_center_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
        return
    }
    hosts.forEach { target ->
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(target.displayName, style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.server_center_host_summary, target.sshUserName, target.sshHost, target.sshPort, hostStatus(target)), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = { onManage(target.hostId) }) { Text(stringResource(R.string.server_center_manage_host)) }
            }
        }
    }
}

@Composable
private fun VerificationNotice(verification: ServerCenterSshVerification?) = when (verification) {
    is ServerCenterSshVerification.Trusted -> Text(stringResource(R.string.server_center_ssh_verified, verification.fingerprint.orEmpty()), color = MaterialTheme.colorScheme.primary)
    is ServerCenterSshVerification.NeedsTrust -> Unit
    is ServerCenterSshVerification.KeyChanged -> Text(stringResource(R.string.server_center_host_key_changed, verification.observation.groupedFingerprint), color = MaterialTheme.colorScheme.error)
    ServerCenterSshVerification.Failed -> Text(stringResource(R.string.server_center_ssh_failed), color = MaterialTheme.colorScheme.error)
    null -> Unit
}

@Composable
private fun hostStatus(target: ServerHostTarget): String = when {
    target.sshVerifiedAtEpochMillis != null -> stringResource(R.string.server_center_status_ssh_verified)
    target.lastVerified == null -> stringResource(R.string.server_center_status_unverified)
    target.lastVerified.installed && target.lastVerified.healthy -> stringResource(R.string.server_center_status_healthy_cached)
    target.lastVerified.installed -> stringResource(R.string.server_center_status_unhealthy_cached)
    else -> stringResource(R.string.server_center_status_not_installed_cached)
}
