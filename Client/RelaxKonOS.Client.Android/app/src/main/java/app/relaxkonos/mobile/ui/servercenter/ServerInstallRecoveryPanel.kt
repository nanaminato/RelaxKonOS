package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerCenterDeploymentClient
import app.relaxkonos.mobile.servercenter.ServerCenterUploadAsset
import app.relaxkonos.mobile.servercenter.ServerDeploymentState
import app.relaxkonos.mobile.servercenter.ServerHostPlatform
import app.relaxkonos.mobile.servercenter.ServerInstallOperationReference
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun ServerInstallRecoveryPanel(hostId: String, knownPlatform: ServerHostPlatform? = null) {
    val viewModel: ServerInstallRecoveryViewModel = viewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val platform = knownPlatform ?: state.platform
    var selectedOperationId by rememberSaveable(hostId) { mutableStateOf<String?>(null) }
    var confirmClear by rememberSaveable(hostId) { mutableStateOf(false) }
    LaunchedEffect(hostId, knownPlatform) { viewModel.refresh(hostId, knownPlatform) }
    DisposableEffect(hostId) { onDispose { viewModel.stop() } }

    HorizontalDivider()
    Text(stringResource(R.string.ssh_deploy_history_title), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.ssh_deploy_history_note), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant)
    if (platform != null) Text(platform.name, style = MaterialTheme.typography.bodySmall)
    Row {
        TextButton(onClick = { viewModel.refresh(hostId, platform) }, enabled = !state.loading) {
            ActionLabel(R.string.common_refresh)
        }
        TextButton(onClick = { confirmClear = true }, enabled = !state.loading && state.hostId == hostId &&
            state.items.any { it.canClearHistory() }) {
            Text(stringResource(R.string.server_operation_clear))
        }
    }
    if (confirmClear) AlertDialog(
        onDismissRequest = { confirmClear = false },
        title = { Text(stringResource(R.string.server_operation_clear)) },
        text = { Text(stringResource(R.string.server_operation_clear_note)) },
        confirmButton = {
            TextButton(onClick = { confirmClear = false; selectedOperationId = null; viewModel.clearCompleted(hostId) }) {
                Text(stringResource(R.string.server_operation_clear))
            }
        },
        dismissButton = { TextButton(onClick = { confirmClear = false }) { Text(stringResource(R.string.common_cancel)) } },
    )
    if (state.loading) CircularProgressIndicator()
    if (state.needsVerification) Text(stringResource(R.string.ssh_deploy_verify_again), color = MaterialTheme.colorScheme.error)
    if (state.incomplete) Text(stringResource(R.string.ssh_deploy_history_incomplete), color = MaterialTheme.colorScheme.error)
    if (state.clearFailed) Text(stringResource(R.string.server_operation_clear_failed), color = MaterialTheme.colorScheme.error)
    if (platform != null && !state.loading && !state.needsVerification && !state.incomplete && state.items.isEmpty()) {
        Text(stringResource(R.string.ssh_deploy_history_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (state.hostId == hostId && state.platform == platform) state.items.forEach { item ->
        Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            HorizontalDivider()
            Text(item.reference.operationId, style = MaterialTheme.typography.bodySmall)
            Text(installReceiptStatus(item), style = MaterialTheme.typography.bodyMedium)
            item.receipt?.let { receipt ->
                Text(stringResource(R.string.operations_stage, receipt.phase.name), style = MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.operations_checked, receipt.timestampUtc), style = MaterialTheme.typography.bodySmall)
                receipt.problemCode?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
            TextButton(onClick = { selectedOperationId = item.reference.operationId }) {
                Text(stringResource(R.string.server_operation_details))
            }
        }
    }
    if (state.hostId == hostId && state.platform == platform) {
        state.items.firstOrNull { it.reference.operationId == selectedOperationId }?.let { item ->
            AlertDialog(
                onDismissRequest = { selectedOperationId = null },
                title = { Text(stringResource(R.string.server_operation_details)) },
                confirmButton = {
                    TextButton(onClick = { selectedOperationId = null }) {
                        Text(stringResource(R.string.common_close))
                    }
                },
                text = {
                androidx.compose.foundation.text.selection.SelectionContainer {
                    Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()),
                        verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text(item.reference.operationId, style = MaterialTheme.typography.bodySmall)
                        Text(installReceiptStatus(item))
                        item.receipt?.let { receipt ->
                            Text("${receipt.kind} · ${receipt.phase} · ${receipt.state}")
                            Text(stringResource(R.string.operations_checked, receipt.timestampUtc))
                            receipt.problemCode?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                            Text(receipt.safeMessage.orEmpty())
                            Text("${receipt.startedAtUtc.orEmpty()} → ${receipt.completedAtUtc.orEmpty()}")
                            val result = receipt.result
                            val snapshot = receipt.snapshot
                            Text(listOfNotNull(receipt.installationId, result?.mode?.name ?: snapshot?.mode?.name,
                                result?.version ?: snapshot?.version, result?.listenUrl ?: snapshot?.listenUrl,
                                result?.installRoot ?: snapshot?.installRoot, result?.dataRoot ?: snapshot?.dataRoot).joinToString("\n"))
                        }
                        OperationLog(hostId, item.reference)
                    }
                }
                },
            )
        }
    }
}

@Composable
private fun installReceiptStatus(item: InstallReceiptItem): String = when (item.check) {
    InstallReceiptCheck.Missing -> stringResource(R.string.operations_missing)
    InstallReceiptCheck.Unavailable -> stringResource(R.string.operations_unverified)
    InstallReceiptCheck.Verified -> when (item.receipt?.state) {
        ServerDeploymentState.Queued, ServerDeploymentState.Running -> stringResource(R.string.operations_running)
        ServerDeploymentState.Succeeded -> stringResource(R.string.operations_succeeded)
        ServerDeploymentState.Failed -> stringResource(R.string.operations_failed)
        ServerDeploymentState.Cancelled -> stringResource(R.string.operations_cancelled)
        ServerDeploymentState.Interrupted -> stringResource(R.string.operations_interrupted)
        null -> stringResource(R.string.operations_unverified)
    }
}

@Composable
private fun OperationLog(hostId: String, reference: ServerInstallOperationReference) {
    val model: ServerInstallRecoveryViewModel = viewModel()
    var log by androidx.compose.runtime.remember(reference.operationId) { mutableStateOf<String?>(null) }
    var failed by androidx.compose.runtime.remember(reference.operationId) { mutableStateOf(false) }
    LaunchedEffect(hostId, reference.operationId) {
        model.readLog(hostId, reference).fold(
            onSuccess = { log = it },
            onFailure = { failed = true },
        )
    }
    when {
        failed -> Text(stringResource(R.string.operations_unverified), color = MaterialTheme.colorScheme.error)
        log == null -> CircularProgressIndicator()
        log!!.isEmpty() -> Text(stringResource(R.string.server_operation_no_log))
        else -> Text(log!!, style = MaterialTheme.typography.bodySmall)
    }
}
