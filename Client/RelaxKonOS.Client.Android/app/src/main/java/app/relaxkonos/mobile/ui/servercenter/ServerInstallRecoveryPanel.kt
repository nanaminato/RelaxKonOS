package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerCenterDeploymentClient
import app.relaxkonos.mobile.servercenter.ServerCenterUploadAsset
import app.relaxkonos.mobile.servercenter.ServerDeploymentOperation
import app.relaxkonos.mobile.servercenter.ServerDeploymentReceiptMissingException
import app.relaxkonos.mobile.servercenter.ServerDeploymentState
import app.relaxkonos.mobile.servercenter.ServerHostPlatform
import app.relaxkonos.mobile.servercenter.ServerInstallOperationReference
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal enum class InstallReceiptCheck { Verified, Missing, Unavailable }

internal data class InstallReceiptItem(
    val reference: ServerInstallOperationReference,
    val check: InstallReceiptCheck,
    val receipt: ServerDeploymentOperation? = null,
)

internal data class InstallRecoveryState(
    val hostId: String? = null,
    val platform: ServerHostPlatform? = null,
    val loading: Boolean = false,
    val needsVerification: Boolean = false,
    val incomplete: Boolean = false,
    val items: List<InstallReceiptItem> = emptyList(),
)

class ServerInstallRecoveryViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    private val mutableState = MutableStateFlow(InstallRecoveryState())
    internal val state = mutableState.asStateFlow()
    private var refreshJob: Job? = null
    private var generation = 0

    fun refresh(hostId: String, platform: ServerHostPlatform) {
        refreshJob?.cancel()
        val request = ++generation
        mutableState.value = InstallRecoveryState(hostId = hostId, platform = platform, loading = true)
        refreshJob = viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) {
                if (request == generation) mutableState.value = InstallRecoveryState(hostId, platform, needsVerification = true)
                return@launch
            }
            try {
                val result = withContext(Dispatchers.IO) { readReceipts(hostId, platform, secret) }
                if (request == generation) mutableState.value = result
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (request == generation) mutableState.value = InstallRecoveryState(hostId, platform, incomplete = true)
            } finally {
                secret.fill('\u0000')
            }
        }
    }

    fun stop() {
        generation++
        refreshJob?.cancel()
        refreshJob = null
    }

    private suspend fun readReceipts(hostId: String, platform: ServerHostPlatform, secret: CharArray): InstallRecoveryState {
        val credential = SshCredential(SshCredentialKind.Password, secret, null)
        try {
            return container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                val key = requireNotNull(session.observedHostKey) { "A verified SSH host key is required." }
                val index = container.serverInstallOperations
                val local = index.forTrustedHost(session.target, key).filter { it.platform == platform }
                val client = ServerCenterDeploymentClient(session.sshTransport)
                val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform)
                val lookup = client.stageLookup(platform, launcher)
                var incomplete = false
                val remoteIds = try {
                    client.list(lookup)
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (_: Exception) {
                    incomplete = true
                    emptyList()
                }
                val localById = local.associateBy { it.operationId }
                val allIds = (remoteIds + local.map { it.operationId }).distinct()
                if (remoteIds.size == 20 || allIds.size > 100) incomplete = true
                val ids = allIds.take(100)
                val items = ids.map { id ->
                    val reference = localById[id] ?: try {
                        index.record(session.target, key, id, platform)
                    } catch (_: IllegalStateException) {
                        incomplete = true
                        ServerInstallOperationReference(session.target.hostId, key.algorithm, key.fingerprint,
                            id, platform, System.currentTimeMillis())
                    }
                    try {
                        val receipt = client.query(lookup, id)
                        try {
                            index.markVerified(session.target, reference, key, System.currentTimeMillis())
                        } catch (_: IllegalStateException) {
                            incomplete = true
                        }
                        InstallReceiptItem(reference, InstallReceiptCheck.Verified, receipt)
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (_: ServerDeploymentReceiptMissingException) {
                        InstallReceiptItem(reference, InstallReceiptCheck.Missing)
                    } catch (_: Exception) {
                        InstallReceiptItem(reference, InstallReceiptCheck.Unavailable)
                    }
                }
                InstallRecoveryState(hostId, platform, incomplete = incomplete, items = items)
            }
        } finally {
            credential.clear()
        }
    }
}

@Composable
internal fun ServerInstallRecoveryPanel(hostId: String) {
    val viewModel: ServerInstallRecoveryViewModel = viewModel()
    val state by viewModel.state.collectAsState()
    var platformName by rememberSaveable(hostId) { mutableStateOf("") }
    val platform = ServerHostPlatform.entries.firstOrNull { it.name == platformName }
    LaunchedEffect(hostId, platform) { if (platform != null) viewModel.refresh(hostId, platform) }
    DisposableEffect(hostId) { onDispose { viewModel.stop() } }

    HorizontalDivider()
    Text(stringResource(R.string.ssh_deploy_history_title), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.ssh_deploy_history_note), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant)
    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        FilterChip(platform == ServerHostPlatform.Linux, { platformName = ServerHostPlatform.Linux.name },
            { Text(stringResource(R.string.ssh_deploy_host_linux)) })
        FilterChip(platform == ServerHostPlatform.Windows, { platformName = ServerHostPlatform.Windows.name },
            { Text(stringResource(R.string.ssh_deploy_host_windows)) })
    }
    if (platform != null) {
        TextButton(onClick = { viewModel.refresh(hostId, platform) }, enabled = !state.loading) {
            Text(stringResource(R.string.common_refresh))
        }
    }
    if (state.loading) CircularProgressIndicator()
    if (state.needsVerification) Text(stringResource(R.string.ssh_deploy_verify_again), color = MaterialTheme.colorScheme.error)
    if (state.incomplete) Text(stringResource(R.string.ssh_deploy_history_incomplete), color = MaterialTheme.colorScheme.error)
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
