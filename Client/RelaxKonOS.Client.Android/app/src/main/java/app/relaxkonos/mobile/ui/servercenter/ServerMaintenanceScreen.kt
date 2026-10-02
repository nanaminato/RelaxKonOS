package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
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
    fun run(hostId: String, kind: ServerDeploymentKind = ServerDeploymentKind.Status, purge: Boolean = false, sudo: String = "",
        repairCertificate: Boolean = false, certificateIdentities: String = "") {
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
                            action(kind, maintenanceOptions(kind, mode, requireNotNull(before.installationId), purge,
                                repairCertificate, certificateIdentities), password)
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
    var page by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    val pageScroll = remember(page) { androidx.compose.foundation.ScrollState(0) }
    var uninstall by remember { mutableStateOf(false) }
    var purge by remember { mutableStateOf(false) }
    var sudo by remember { mutableStateOf("") }
    var repairCertificate by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var certificateIdentities by rememberSaveable(host?.hostId) {
        mutableStateOf(listOf("localhost", "127.0.0.1", host?.sshHost.orEmpty()).filter(String::isNotBlank).distinct().joinToString(","))
    }
    var confirmCertificateRepair by remember(host?.hostId) { mutableStateOf(false) }
    val identitiesValid = runCatching { normalizeRepairCertificateIdentities(certificateIdentities) }.isSuccess
    androidx.activity.compose.BackHandler(enabled = wizard || page != 0) {
        if (!installing && !state.busy) {
            if (wizard) wizard = false else page = 0
        }
    }
    LaunchedEffect(host?.hostId, wizard) { if (host != null && !wizard) model.run(host.hostId) }
    Column(modifier.fillMaxSize().imePadding()) {
        if (wizard) {
            TextButton(onClick = { wizard = false }, enabled = !installing) { Text(stringResource(R.string.common_back)) }
            DeploymentSetupScreen(host, Modifier.weight(1f), onBusyChanged = { installing = it })
        } else Column(Modifier.fillMaxSize().verticalScroll(pageScroll).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Text(stringResource(R.string.server_maintenance_title), style = MaterialTheme.typography.titleLarge)
            val address = host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty()
            val name = host?.displayName.orEmpty()
            Text(if (name.isBlank() || name == address || name == host?.sshHost) address else "$name · $address",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            PrimaryScrollableTabRow(selectedTabIndex = page, edgePadding = Spacing.xs,
                containerColor = androidx.compose.ui.graphics.Color.Transparent,
                contentColor = MaterialTheme.colorScheme.primary) {
                listOf(R.string.server_maintenance_overview, R.string.server_maintenance_environment,
                    R.string.server_maintenance_actions, R.string.server_maintenance_history).forEachIndexed { index, title ->
                    Tab(selected = page == index, onClick = { page = index }, text = { Text(stringResource(title)) })
                }
            }
            TextButton(onClick = { host?.let { model.run(it.hostId, sudo = sudo) } }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_check)) }
            if (state.busy) CircularProgressIndicator()
            OperationMessageDialog(state.error.takeUnless { state.busy })
            if (page != 3) state.snapshot?.let { snapshot ->
                if (page == 0) {
                    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(if (snapshot.installed) R.string.ssh_workspace_deploy_installed else R.string.server_maintenance_absent))
                    if (snapshot.installed) {
                        Text(stringResource(if (snapshot.healthy) R.string.server_maintenance_healthy else R.string.server_maintenance_unhealthy))
                        MaintenanceDetail(R.string.server_maintenance_version, snapshot.version)
                        MaintenanceDetail(R.string.server_maintenance_mode, snapshot.mode?.name)
                        MaintenanceDetail(R.string.server_maintenance_endpoint, snapshot.listenUrl)
                    }
                    MaintenanceDetail(R.string.server_maintenance_checked_at, snapshot.verifiedAtUtc)
                    } }
                    if (snapshot.installed) {
                        Button(onClick = { page = 2 }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_actions)) }
                        OutlinedButton(onClick = { page = 1 }) { Text(stringResource(R.string.server_maintenance_environment)) }
                    } else Button(onClick = { wizard = true }, enabled = !state.busy && state.probe?.osSupported == true) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
                }
                if (page == 1) state.probe?.let {
                    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text("${it.hostPlatform} · ${it.architecture} · ${it.osId.orEmpty()} ${it.osVersion.orEmpty()}\n${it.runtimeIdentifier}\n${it.verifiedAtUtc}")
                    Text(stringResource(R.string.server_maintenance_probe, it.osSupported.toString(), it.sudoAvailable.toString(),
                        it.systemdAvailable.toString(), it.diskAvailableBytes?.toString().orEmpty(), it.requestedPortAvailable?.toString().orEmpty(), it.missingDependencies.joinToString()))
                    HorizontalDivider()
                    MaintenanceDetail(R.string.server_maintenance_install_root, snapshot.installRoot)
                    MaintenanceDetail(R.string.server_maintenance_data_root, snapshot.dataRoot)
                    MaintenanceDetail(R.string.server_maintenance_services, snapshot.serviceNames.joinToString())
                    MaintenanceDetail(R.string.server_maintenance_identity, snapshot.installationId)
                    } }
                }
                if (page == 2 && snapshot.installed) {
                    Text(stringResource(R.string.server_maintenance_actions_note), style = MaterialTheme.typography.bodySmall)
                    PasswordTextField(sudo, { sudo = it }, stringResource(R.string.ssh_workspace_deploy_sudo_password))
                    Button(onClick = { wizard = true }, enabled = !state.busy) { Text(stringResource(R.string.installation_kind_upgrade)) }
                    val certificateRepairSupported = snapshot.mode == ServerInstallMode.LinuxSystem || snapshot.mode == ServerInstallMode.WindowsSystem
                    if (certificateRepairSupported) {
                        Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                            Checkbox(repairCertificate, { repairCertificate = it }, enabled = !state.busy)
                            Text(stringResource(R.string.server_maintenance_repair_certificate), modifier = Modifier.weight(1f))
                        }
                        if (repairCertificate) {
                            Text(stringResource(R.string.server_maintenance_repair_certificate_note), style = MaterialTheme.typography.bodySmall)
                            OutlinedTextField(certificateIdentities, { certificateIdentities = it },
                                label = { Text(stringResource(R.string.ssh_workspace_deploy_certificate_names)) },
                                modifier = Modifier.fillMaxWidth(), enabled = !state.busy, isError = !identitiesValid)
                            if (!identitiesValid) Text(stringResource(R.string.server_maintenance_repair_certificate_invalid), color = MaterialTheme.colorScheme.error)
                        }
                    }
                    OutlinedButton(onClick = {
                        if (repairCertificate && certificateRepairSupported) confirmCertificateRepair = true
                        else { host?.let { model.run(it.hostId, ServerDeploymentKind.Repair, sudo = sudo) }; sudo = "" }
                    }, enabled = !state.busy && (!repairCertificate || !certificateRepairSupported || identitiesValid)) {
                        Text(stringResource(R.string.server_maintenance_repair))
                    }
                    OutlinedButton(onClick = { purge = false; uninstall = true }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_uninstall)) }
                } else if (page == 2) Button(onClick = { wizard = true }, enabled = !state.busy && state.probe?.osSupported == true) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
            }
            if (state.complete) Text(stringResource(R.string.server_maintenance_complete))
            if (page == 3 && host != null) ServerInstallRecoveryPanel(host.hostId,
                state.probe?.let { if (it.hostPlatform.equals("linux", true)) ServerHostPlatform.Linux else ServerHostPlatform.Windows })
        }
    }
    if (uninstall) AlertDialog(onDismissRequest = { uninstall = false }, title = { Text(stringResource(R.string.server_maintenance_uninstall)) },
        text = { Column { Text(stringResource(R.string.server_maintenance_uninstall_note)); Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(purge, { purge = it }); Text(stringResource(R.string.server_maintenance_purge)) } } },
        confirmButton = { TextButton(onClick = { uninstall = false; host?.let { model.run(it.hostId, ServerDeploymentKind.Uninstall, purge, sudo) }; sudo = "" }) { Text(stringResource(R.string.server_maintenance_uninstall)) } },
        dismissButton = { TextButton(onClick = { uninstall = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (confirmCertificateRepair) AlertDialog(onDismissRequest = { confirmCertificateRepair = false },
        title = { Text(stringResource(R.string.server_maintenance_repair_certificate)) },
        text = { Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.server_maintenance_repair_certificate_note))
            Text(certificateIdentities)
        } },
        confirmButton = { TextButton(onClick = {
            confirmCertificateRepair = false
            host?.let { model.run(it.hostId, ServerDeploymentKind.Repair, sudo = sudo,
                repairCertificate = true, certificateIdentities = certificateIdentities) }
            sudo = ""
        }, enabled = !state.busy && identitiesValid) { Text(stringResource(R.string.server_maintenance_repair)) } },
        dismissButton = { TextButton(onClick = { confirmCertificateRepair = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable
private fun MaintenanceDetail(label: Int, value: String?) {
    Column {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        androidx.compose.foundation.text.selection.SelectionContainer { Text(value?.takeIf { it.isNotBlank() } ?: "—") }
    }
}
