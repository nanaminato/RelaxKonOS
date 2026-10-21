package app.relaxkonos.mobile.ui.servercenter
import app.relaxkonos.mobile.ui.common.CheckboxOption
import app.relaxkonos.mobile.ui.common.text

import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.lifecycle.viewmodel.initializer
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
    val container = app.relaxkonos.mobile.ui.common.appContainer()
    val assets = androidx.compose.ui.platform.LocalContext.current.applicationContext.assets
    val model: ServerMaintenanceViewModel = viewModel(key = "maintenance-${host?.hostId}", factory =
        viewModelFactory {
            initializer { ServerMaintenanceViewModel(RepositoryMaintenanceActions(container, assets), container.maintenancePendingJournal) }
        })
    ServerMaintenanceContent(host, model, modifier)
}

@Composable
internal fun ServerMaintenanceContent(host: ServerHostTarget?, model: ServerMaintenanceViewModel, modifier: Modifier = Modifier) {
    val state by model.state.collectAsStateWithLifecycle()
    var wizard by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var installing by remember(host?.hostId) { mutableStateOf(false) }
    var page by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    val pageScroll = remember(page) { androidx.compose.foundation.ScrollState(0) }
    var purge by remember(host?.hostId) { mutableStateOf(false) }
    var removeComponents by remember(host?.hostId) { mutableStateOf(setOf<String>()) }
    var sudo by remember(host?.hostId) { mutableStateOf("") }
    var addFirewallRule by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var repairCertificate by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var certificateIdentities by rememberSaveable(host?.hostId) {
        mutableStateOf(listOf("localhost", "127.0.0.1", host?.sshHost.orEmpty()).filter(String::isNotBlank).distinct().joinToString(","))
    }
    var repairPackagePath by remember(host?.hostId) { mutableStateOf("") }
    val activity = androidx.activity.compose.LocalActivity.current
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(host?.hostId, model, lifecycle) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_PAUSE) { model.setReviewActive(false); sudo = "" }
            if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME) model.setReviewActive(true)
        }
        lifecycle.addObserver(observer)
        onDispose {
            lifecycle.removeObserver(observer); model.setReviewActive(false)
            if (activity?.isChangingConfigurations != true) model.clearConfiguration()
        }
    }
    DisposableEffect(host?.hostId, page, wizard, model) {
        model.clearReview(); model.invalidateConfiguration()
        onDispose { model.clearReview(); model.invalidateConfiguration() }
    }
    val identitiesValid = runCatching { normalizeRepairCertificateIdentities(certificateIdentities) }.isSuccess
    androidx.activity.compose.BackHandler(enabled = wizard || page != 0) {
        if (!installing && !state.busy) {
            if (wizard) wizard = false else page = 0
        }
    }
    LaunchedEffect(host?.hostId, wizard) { if (host != null && !wizard) model.check(host.hostId) }
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
                TextButton(onClick = { host?.let { model.check(it.hostId, sudo = sudo) } }, enabled = !state.busy) { Text(stringResource(R.string.server_maintenance_check)) }
                OperationMessageDialog(state.error.takeUnless { state.busy }?.text(), eventKey = state.feedbackVersion)
                state.pending?.let { pending ->
                    Text(stringResource(R.string.server_maintenance_pending), color = MaterialTheme.colorScheme.error)
                    Text(pending.reference.operationId, style = MaterialTheme.typography.bodySmall)
                    key(pending.reference.hostId, pending.reference.operationId) {
                        TextButton(onClick = { model.recover(pending) }, enabled = !state.busy) {
                            Text(stringResource(R.string.server_maintenance_reconcile))
                        }
                    }
                }
                if (state.recovered != null) Text(stringResource(R.string.server_maintenance_reconciled))
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
                        } else Button(onClick = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true }, enabled = state.pending == null && !state.busy && state.probe?.runtimeIdentifier != null) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
                    }
                    if (page == 1) state.probe?.let {
                        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text("${it.hostPlatform} · ${it.architecture} · ${it.osId.orEmpty()} ${it.osVersion.orEmpty()}\n${it.runtimeIdentifier}\n${it.verifiedAtUtc}")
                        Text(stringResource(R.string.server_maintenance_probe, it.osSupported.toString(), it.privilegeElevationAvailable.toString(),
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
                        PasswordTextField(sudo, { sudo = it }, stringResource(R.string.ssh_workspace_deploy_sudo_password), enabled = !state.busy)
                        Button(onClick = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true }, enabled = state.pending == null && !state.busy) { Text(stringResource(R.string.installation_kind_upgrade)) }
                        OutlinedButton(onClick = { host?.let { model.openConfiguration(it.hostId, componentsOnly = true) } }, enabled = state.pending == null && !state.busy && snapshot.guardianEnabled != null) { Text(stringResource(R.string.server_configuration_components)) }
                        OutlinedButton(onClick = { host?.let { model.openConfiguration(it.hostId, componentsOnly = false) } }, enabled = state.pending == null && !state.busy && snapshot.guardianEnabled != null) { Text(stringResource(R.string.server_configuration_settings)) }
                        OutlinedButton(onClick = {
                            addFirewallRule = false
                            repairCertificate = false
                            host?.let { model.requestReview(it.hostId, ServerDeploymentKind.Repair) }
                        }, enabled = state.pending == null && !state.busy) { Text(stringResource(R.string.server_maintenance_repair)) }
                        if (snapshot.mode != ServerInstallMode.LinuxUser) OutlinedButton(onClick = { host?.let { model.firewall(it.hostId, removeRules = false, sudo = sudo) }; sudo = "" }, enabled = state.pending == null && !state.busy) { Text(stringResource(R.string.server_configuration_ensure_firewall)) }
                        if (snapshot.mode != ServerInstallMode.LinuxUser) OutlinedButton(onClick = { host?.let { model.firewall(it.hostId, removeRules = true, sudo = sudo) }; sudo = "" }, enabled = state.pending == null && !state.busy) { Text(stringResource(R.string.server_configuration_remove_firewall)) }
                        OutlinedButton(onClick = { purge = false; removeComponents = emptySet(); host?.let { model.requestReview(it.hostId, ServerDeploymentKind.Uninstall) } }, enabled = state.pending == null && !state.busy) { Text(stringResource(R.string.server_maintenance_uninstall)) }
                    } else if (page == 2) Button(onClick = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true }, enabled = state.pending == null && !state.busy && state.probe?.runtimeIdentifier != null) { Text(stringResource(R.string.ssh_workspace_deploy_install)) }
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
    state.configuration?.let { editor ->
        val version = state.configurationVersion
        ServerConfigurationDialog(editor, state.busy, version,
            onDismiss = { if (model.dismissConfiguration(editor, version)) sudo = "" },
            onApply = { draft -> if (model.configure(editor, version, draft, sudo)) sudo = "" })
    }
    state.review?.takeIf { it.kind == ServerDeploymentKind.Uninstall }?.let { review ->
        val cancelUninstall = { if (model.dismissReview(review)) sudo = "" }
        AlertDialog(onDismissRequest = cancelUninstall, title = { Text(stringResource(R.string.server_maintenance_uninstall)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) {
            Text(host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty(), style = MaterialTheme.typography.bodySmall)
            Text(review.snapshot.installationId.orEmpty(), style = MaterialTheme.typography.bodySmall)
            Text(stringResource(R.string.server_maintenance_uninstall_note))
            Text(stringResource(R.string.server_components_note))
            listOf("smb" to R.string.server_remove_smb, "nginx" to R.string.server_remove_nginx,
                "frp" to R.string.server_remove_frp, "mihomo" to R.string.server_remove_mihomo).forEach { (component, label) ->
                CheckboxOption(component in removeComponents, stringResource(label), !state.busy) { checked ->
                        removeComponents = if (checked) removeComponents + component else removeComponents - component
                        if (!checked) purge = false
                }
            }
            CheckboxOption(purge, stringResource(R.string.server_maintenance_purge), !state.busy) { purge = it; if (it) removeComponents = setOf("smb", "nginx", "frp", "mihomo") }
        } },
        confirmButton = { TextButton(enabled = host != null && !state.busy, onClick = { if (model.submitReview(review, purge, sudo, removeComponents = listOf("smb", "nginx", "frp", "mihomo").filter { it in removeComponents }.joinToString(","))) sudo = "" }) { Text(stringResource(R.string.server_maintenance_uninstall)) } },
        dismissButton = { TextButton(onClick = cancelUninstall) { Text(stringResource(R.string.common_cancel)) } }) }
    state.review?.takeIf { it.kind == ServerDeploymentKind.Repair }?.let { review ->
        val cancelRepair = { if (model.dismissReview(review)) sudo = "" }
        AlertDialog(
        onDismissRequest = cancelRepair,
        title = { Text(stringResource(R.string.server_maintenance_repair)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Text(host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty(), style = MaterialTheme.typography.bodySmall)
            Text(review.snapshot.installationId.orEmpty(), style = MaterialTheme.typography.bodySmall)
            PasswordTextField(sudo, { sudo = it }, stringResource(R.string.ssh_workspace_deploy_sudo_password), enabled = !state.busy)
            OutlinedTextField(repairPackagePath, { repairPackagePath = it }, enabled = !state.busy, label = { Text(stringResource(R.string.server_configuration_repair_package)) })
            val certificateRepairSupported = review.snapshot.mode == ServerInstallMode.LinuxSystem ||
                review.snapshot.mode == ServerInstallMode.WindowsSystem
            if (certificateRepairSupported) {
                CheckboxOption(addFirewallRule, stringResource(R.string.server_install_firewall_choice), !state.busy) { addFirewallRule = it }
                Text(stringResource(R.string.server_install_firewall_help), style = MaterialTheme.typography.bodySmall)
                CheckboxOption(repairCertificate, stringResource(R.string.server_maintenance_repair_certificate), !state.busy) { repairCertificate = it }
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
            val certificateRepairSupported = review.snapshot.mode == ServerInstallMode.LinuxSystem ||
                review.snapshot.mode == ServerInstallMode.WindowsSystem
            if (model.submitReview(review, sudo = sudo,
                repairCertificate = repairCertificate && certificateRepairSupported,
                certificateIdentities = certificateIdentities,
                addFirewallRule = addFirewallRule && certificateRepairSupported, repairPackagePath = repairPackagePath)) sudo = ""
        }, enabled = !state.busy && host != null && (!repairCertificate || identitiesValid)) {
            Text(stringResource(R.string.server_maintenance_repair))
        } },
        dismissButton = { TextButton(onClick = cancelRepair, enabled = !state.busy) {
            Text(stringResource(R.string.common_cancel))
        } }) }
}

@Composable
private fun MaintenanceDetail(label: Int, value: String?) {
    Column {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        androidx.compose.foundation.text.selection.SelectionContainer { Text(value?.takeIf { it.isNotBlank() } ?: "—") }
    }
}
