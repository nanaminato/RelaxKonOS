package app.relaxkonos.mobile.ui.servercenter

import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
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
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.*

@Composable
internal fun ServerMaintenanceScreen(host: ServerHostTarget?, modifier: Modifier = Modifier) {
    val model: ServerMaintenanceViewModel = viewModel(key = "maintenance-${host?.hostId}")
    val state by model.state.collectAsStateWithLifecycle()
    var wizard by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var installing by remember { mutableStateOf(false) }
    var page by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    val pageScroll = remember(page) { androidx.compose.foundation.ScrollState(0) }
    var uninstall by remember { mutableStateOf(false) }
    var purge by remember { mutableStateOf(false) }
    var removeComponents by remember(host?.hostId) { mutableStateOf(setOf<String>()) }
    var sudo by remember { mutableStateOf("") }
    var addFirewallRule by rememberSaveable { mutableStateOf(false) }
    var repairCertificate by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var certificateIdentities by rememberSaveable(host?.hostId) {
        mutableStateOf(listOf("localhost", "127.0.0.1", host?.sshHost.orEmpty()).filter(String::isNotBlank).distinct().joinToString(","))
    }
    var repair by remember(host?.hostId) { mutableStateOf(false) }
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
        } else {
            if (state.busy) Column(
                Modifier.fillMaxWidth().padding(Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm),
            ) {
                Text(stringResource(state.progress), style = MaterialTheme.typography.titleSmall)
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
            Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(pageScroll).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
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
                        } else Button(onClick = { wizard = true }, enabled = !state.busy && state.probe?.runtimeIdentifier != null) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
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
                        OutlinedButton(onClick = {
                            addFirewallRule = false
                            repairCertificate = false
                            repair = true
                        }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_repair)) }
                        OutlinedButton(onClick = { purge = false; removeComponents = emptySet(); uninstall = true }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_uninstall)) }
                    } else if (page == 2) Button(onClick = { wizard = true }, enabled = !state.busy && state.probe?.runtimeIdentifier != null) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
                }
                if (state.complete) {
                    Text(stringResource(R.string.server_maintenance_complete))
                    if (state.firewallStatus == "disabled") Text(stringResource(R.string.server_install_firewall_disabled))
                    if (state.firewallStatus == "ruleAdded") Text(stringResource(R.string.server_install_firewall_added))
                }
                if (page == 3 && host != null) ServerInstallRecoveryPanel(host.hostId,
                    state.probe?.let { if (it.hostPlatform.equals("linux", true)) ServerHostPlatform.Linux else ServerHostPlatform.Windows })
            }
        }
    }
    if (uninstall) AlertDialog(onDismissRequest = { uninstall = false }, title = { Text(stringResource(R.string.server_maintenance_uninstall)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.server_maintenance_uninstall_note))
            Text(stringResource(R.string.server_components_note))
            listOf("smb" to R.string.server_remove_smb, "nginx" to R.string.server_remove_nginx,
                "frp" to R.string.server_remove_frp, "mihomo" to R.string.server_remove_mihomo).forEach { (component, label) ->
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(component in removeComponents, { checked ->
                        removeComponents = if (checked) removeComponents + component else removeComponents - component
                        if (!checked) purge = false
                    })
                    Text(stringResource(label))
                }
            }
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Checkbox(purge, { purge = it; if (it) removeComponents = setOf("smb", "nginx", "frp", "mihomo") })
                Text(stringResource(R.string.server_maintenance_purge))
            }
        } },
        confirmButton = { TextButton(onClick = { uninstall = false; host?.let { model.run(it.hostId, ServerDeploymentKind.Uninstall, purge, sudo, removeComponents = listOf("smb", "nginx", "frp", "mihomo").filter { it in removeComponents }.joinToString(",")) }; sudo = "" }) { Text(stringResource(R.string.server_maintenance_uninstall)) } },
        dismissButton = { TextButton(onClick = { uninstall = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (repair) AlertDialog(
        onDismissRequest = { if (!state.busy) repair = false },
        title = { Text(stringResource(R.string.server_maintenance_repair)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Text(host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty(), style = MaterialTheme.typography.bodySmall)
            PasswordTextField(sudo, { sudo = it }, stringResource(R.string.ssh_workspace_deploy_sudo_password))
            val certificateRepairSupported = state.snapshot?.mode == ServerInstallMode.LinuxSystem ||
                state.snapshot?.mode == ServerInstallMode.WindowsSystem
            if (certificateRepairSupported) {
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(addFirewallRule, { addFirewallRule = it }, enabled = !state.busy)
                    Text(stringResource(R.string.server_install_firewall_choice), modifier = Modifier.weight(1f))
                }
                Text(stringResource(R.string.server_install_firewall_help), style = MaterialTheme.typography.bodySmall)
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
        } },
        confirmButton = { TextButton(onClick = {
            val certificateRepairSupported = state.snapshot?.mode == ServerInstallMode.LinuxSystem ||
                state.snapshot?.mode == ServerInstallMode.WindowsSystem
            repair = false
            host?.let { model.run(it.hostId, ServerDeploymentKind.Repair, sudo = sudo,
                repairCertificate = repairCertificate && certificateRepairSupported,
                certificateIdentities = certificateIdentities,
                addFirewallRule = addFirewallRule && certificateRepairSupported) }
            sudo = ""
        }, enabled = !state.busy && host != null && (!repairCertificate || identitiesValid)) {
            Text(stringResource(R.string.server_maintenance_repair))
        } },
        dismissButton = { TextButton(onClick = { repair = false }, enabled = !state.busy) {
            Text(stringResource(R.string.common_cancel))
        } })
}

@Composable
private fun MaintenanceDetail(label: Int, value: String?) {
    Column {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        androidx.compose.foundation.text.selection.SelectionContainer { Text(value?.takeIf { it.isNotBlank() } ?: "—") }
    }
}
