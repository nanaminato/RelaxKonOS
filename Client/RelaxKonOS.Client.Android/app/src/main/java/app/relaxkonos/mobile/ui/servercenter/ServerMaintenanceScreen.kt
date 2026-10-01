package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

internal data class MaintenanceState(val busy: Boolean = false, val snapshot: ServerHostSnapshot? = null,
    val probe: ServerHostProbe? = null, val error: String? = null, val complete: Boolean = false)

internal fun maintenanceResultValid(kind: ServerDeploymentKind, snapshot: ServerHostSnapshot): Boolean = when (kind) {
    ServerDeploymentKind.Uninstall -> !snapshot.installed
    ServerDeploymentKind.Repair -> snapshot.installed && snapshot.healthy
    else -> true
}

internal class ServerMaintenanceViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as RelaxKonApplication).container
    private val mutable = MutableStateFlow(MaintenanceState())
    val state = mutable.asStateFlow()
    fun run(hostId: String, kind: ServerDeploymentKind = ServerDeploymentKind.Status, purge: Boolean = false, sudo: String = "") {
        if (mutable.value.busy) return
        mutable.value = mutable.value.copy(busy = true, error = null, complete = false)
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) { mutable.value = MaintenanceState(error = getApplication<Application>().getString(R.string.ssh_workspace_deploy_verify)); return@launch }
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
            try {
                val result = withContext(Dispatchers.IO) {
                    container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                        val platform = if (session.sshTransport.run("uname -s").let { it.succeeded && it.standardOutput.trim() == "Linux" }) ServerHostPlatform.Linux else ServerHostPlatform.Windows
                        val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform)
                        val client = ServerCenterDeploymentClient(session.sshTransport)
                        val key = requireNotNull(session.observedHostKey)
                        suspend fun action(action: ServerDeploymentKind, options: ServerDeploymentOptions? = null, password: String? = null): ServerDeploymentOperation {
                            val id = UUID.randomUUID().toString()
                            val staged = client.stage(ServerDeploymentRequest(ServerDeploymentProtocol.VERSION, id, action, options), platform, launcher)
                            val reference = container.serverInstallOperations.record(session.target, key, id, platform)
                            val receipt = client.execute(staged, password)
                            container.serverInstallOperations.markVerified(session.target, reference, key, System.currentTimeMillis())
                            check(receipt.state == ServerDeploymentState.Succeeded) { receipt.problemCode ?: "server-deployment.failed" }
                            return receipt
                        }
                        val probe = requireNotNull(action(ServerDeploymentKind.Probe,
                            ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, serverPort = 5000)).probe)
                        val mode = probe.existingMode ?: if (platform == ServerHostPlatform.Windows) ServerInstallMode.WindowsSystem else if (probe.elevated || probe.sudoAvailable) ServerInstallMode.LinuxSystem else ServerInstallMode.LinuxUser
                        val password = if (mode == ServerInstallMode.LinuxSystem && !probe.elevated) sudo.ifEmpty { String(secret) } else null
                        val options = ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode)
                        val before = requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        if (kind != ServerDeploymentKind.Status) {
                            check(before.installed && ServerInstallationId.isValid(before.installationId))
                            action(kind, options.copy(expectedInstallationId = before.installationId, confirmed = true,
                                retention = if (purge) ServerDataRetention.Delete else ServerDataRetention.Retain), password)
                        }
                        val after = if (kind == ServerDeploymentKind.Status) before else requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        container.serverCenter.recordVerifiedSnapshot(hostId, after)
                        check(maintenanceResultValid(kind, after)) { "server-deployment.postcondition_failed" }
                        MaintenanceState(snapshot = after, probe = probe, complete = kind != ServerDeploymentKind.Status)
                    }
                }
                mutable.value = result
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { mutable.value = mutable.value.copy(busy = false, snapshot = null, error = error.message) }
            finally { credential.clear(); secret.fill('\u0000') }
        }
    }
}

@Composable
internal fun ServerMaintenanceScreen(host: ServerHostTarget?, modifier: Modifier = Modifier) {
    val model: ServerMaintenanceViewModel = viewModel(key = "maintenance-${host?.hostId}")
    val state by model.state.collectAsState()
    var wizard by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var installing by remember { mutableStateOf(false) }
    var history by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var uninstall by remember { mutableStateOf(false) }
    var purge by remember { mutableStateOf(false) }
    var sudo by remember { mutableStateOf("") }
    LaunchedEffect(host?.hostId, wizard) { if (host != null && !wizard) model.run(host.hostId) }
    Column(modifier.fillMaxSize().imePadding()) {
        if (wizard) {
            TextButton(onClick = { wizard = false }, enabled = !installing) { Text(stringResource(R.string.common_back)) }
            DeploymentSetupScreen(host, Modifier.weight(1f), onBusyChanged = { installing = it })
        } else Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Text(stringResource(R.string.server_maintenance_title), style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = { host?.let { model.run(it.hostId, sudo = sudo) } }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_check)) }
            if (state.busy) CircularProgressIndicator()
            state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            state.snapshot?.let { snapshot ->
                Text(stringResource(if (snapshot.installed) R.string.ssh_workspace_deploy_installed else R.string.server_maintenance_absent))
                state.probe?.let {
                    Text("${it.hostPlatform} · ${it.architecture} · ${it.osId.orEmpty()} ${it.osVersion.orEmpty()}\n${it.runtimeIdentifier}\n${it.verifiedAtUtc}")
                    Text(stringResource(R.string.server_maintenance_probe, it.osSupported.toString(), it.sudoAvailable.toString(),
                        it.systemdAvailable.toString(), it.diskAvailableBytes?.toString().orEmpty(), it.requestedPortAvailable?.toString().orEmpty(), it.missingDependencies.joinToString()))
                }
                if (snapshot.installed) {
                    Text("${snapshot.mode} · ${snapshot.version.orEmpty()}\n${snapshot.listenUrl.orEmpty()}\n${snapshot.installRoot.orEmpty()}\n${snapshot.dataRoot.orEmpty()}\n${snapshot.serviceNames.joinToString()}\n${snapshot.installationId.orEmpty()}")
                    Text(stringResource(if (snapshot.healthy) R.string.server_maintenance_healthy else R.string.server_maintenance_unhealthy))
                    PasswordTextField(sudo, { sudo = it }, stringResource(R.string.ssh_workspace_deploy_sudo_password))
                    Button(onClick = { wizard = true }, enabled = !state.busy) { Text(stringResource(R.string.installation_kind_upgrade)) }
                    OutlinedButton(onClick = { host?.let { model.run(it.hostId, ServerDeploymentKind.Repair, sudo = sudo) }; sudo = "" }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_repair)) }
                    OutlinedButton(onClick = { purge = false; uninstall = true }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_uninstall)) }
                } else Button(onClick = { wizard = true }, enabled = !state.busy && state.probe?.osSupported == true) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
            }
            if (state.complete) Text(stringResource(R.string.server_maintenance_complete))
            TextButton(onClick = { history = !history }) { Text(stringResource(R.string.server_maintenance_history)) }
            if (history && host != null) ServerInstallRecoveryPanel(host.hostId)
        }
    }
    if (uninstall) AlertDialog(onDismissRequest = { uninstall = false }, title = { Text(stringResource(R.string.server_maintenance_uninstall)) },
        text = { Column { Text(stringResource(R.string.server_maintenance_uninstall_note)); Row { Checkbox(purge, { purge = it }); Text(stringResource(R.string.server_maintenance_purge)) } } },
        confirmButton = { TextButton(onClick = { uninstall = false; host?.let { model.run(it.hostId, ServerDeploymentKind.Uninstall, purge, sudo) }; sudo = "" }) { Text(stringResource(R.string.server_maintenance_uninstall)) } },
        dismissButton = { TextButton(onClick = { uninstall = false }) { Text(stringResource(R.string.common_cancel)) } })
}
