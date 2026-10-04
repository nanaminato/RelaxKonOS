package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import app.relaxkonos.mobile.data.NginxDiagnostics
import androidx.compose.ui.Alignment
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun NginxManager(onChanged: () -> Unit, section: String, onRecords: () -> Unit) {
    val model: NginxViewModel = viewModel()
    val state = model.state
    val sessionEpoch = model.sessionEpoch
    val owner = appContainer().activeSession
    val canManage = owner?.privilegedOperations == true
    var installDialog by remember(owner, sessionEpoch) { mutableStateOf(false) }
    var diagnostics by remember { mutableStateOf<String?>(null) }
    val clipboard = LocalClipboardManager.current
    var recoveryDialog by remember(owner, sessionEpoch) { mutableStateOf(false) }
    var instanceDetailsVisible by remember(owner, sessionEpoch) { mutableStateOf(false) }
    var confirmation by remember(owner, sessionEpoch) { mutableStateOf<Pair<Int, () -> Unit>?>(null) }
    OperationMessageDialog(if (state.busy) null else state.problemCode?.let { nginxProblemLabel(it) } ?: if (state.uncertain) stringResource(R.string.nginx_uncertain) else null, tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger)
    LaunchedEffect(owner, sessionEpoch, section) { if (owner != null) model.refresh() }
    LaunchedEffect(owner, sessionEpoch) {
        while (owner != null) {
            delay(1500)
            if (!model.state.busy && (model.state.operation?.state?.active == true || model.state.installation?.state?.active == true)) {
                model.pollTasks()
            }
        }
    }
    LaunchedEffect(state.operation?.operationId, state.operation?.state, state.installation?.operationId, state.installation?.state) {
        if (state.operation?.state?.active == false || state.installation?.state?.active == false) {
            onChanged()
        }
    }
    LaunchedEffect(state.siteGeneration) { if (state.siteGeneration > 0) onChanged() }
    TextButton(onClick = { diagnostics = NginxDiagnostics.snapshot() }) { Text(stringResource(R.string.nginx_diagnostics)) }
    diagnostics?.let { trace ->
        AlertDialog(onDismissRequest = { diagnostics = null }, title = { Text(stringResource(R.string.nginx_diagnostics)) },
            text = { Text(trace, modifier = Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), style = MaterialTheme.typography.bodySmall) },
            confirmButton = { TextButton(onClick = { clipboard.setText(AnnotatedString(trace)) }) { Text(stringResource(R.string.nginx_copy_diagnostics)) } },
            dismissButton = { TextButton(onClick = { diagnostics = null }) { Text(stringResource(R.string.common_close)) } })
    }
    SectionCard(stringResource(if (section == "sites") R.string.websites_site_manager else R.string.nginx_title)) {
        if (section != "records" && (state.uncertain || state.pending.isNotEmpty() || state.pendingInstallation || state.operation?.state?.active == true || state.installation?.state?.active == true)) TextButton(onClick = onRecords) { Text(stringResource(R.string.workspace_records_attention)) }
        WorkspaceSection(section != "records") {
        WorkspaceSection(section == "instances") {
        Text(stringResource(R.string.nginx_intro), style = MaterialTheme.typography.bodySmall)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TextButton(onClick = model::refresh, enabled = !state.busy) { Text(stringResource(R.string.nginx_discover)) }
            if (canManage && state.system != HostOperatingSystemKind.Unknown && state.servers.none { it.managementMode == "managed" } && (state.system != HostOperatingSystemKind.Ubuntu || (state.servers.isEmpty() && state.candidates.isEmpty())))
                OutlinedButton(onClick = { installDialog = true }, enabled = !model.mutationsBlocked) { Text(stringResource(R.string.nginx_install)) }
        }
        }
        RefreshProgressIndicator(visible = state.busy || state.loading)

        if (state.catalog?.problemCode?.isNotBlank() == true) Text(nginxProblemLabel(state.catalog.problemCode), color = MaterialTheme.colorScheme.error)
        if (state.servers.isEmpty() && !state.loading) Text(stringResource(R.string.websites_no_servers))
        if (section == "sites") {
            val selected = state.servers.firstOrNull { it.id == state.selectedId }
            if (selected == null) Text(stringResource(R.string.websites_select_instance))
            else {
                Text("Nginx ${selected.version ?: "—"} · ${selected.executablePath}", style = MaterialTheme.typography.bodySmall)
                WebSiteList(selected, state, canManage, model) { message, action -> confirmation = message to action }
            }
        }
        WorkspaceSection(section == "instances") { BoxWithConstraints(Modifier.fillMaxWidth()) {
            val selected = state.servers.firstOrNull { it.id == state.selectedId }
            if (maxWidth >= 600.dp) Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                Column(Modifier.weight(1f)) { InstanceList(state) { model.select(it); instanceDetailsVisible = true } }
                Column(Modifier.weight(2f)) { selected?.let { InstanceDetails(it, state, canManage, model, section, onRecords) { message, action -> confirmation = message to action } } }
            } else Column {
                if (selected == null || !instanceDetailsVisible) InstanceList(state) { model.select(it); instanceDetailsVisible = true } else {
                    TextButton(onClick = { instanceDetailsVisible = false }) { Text(stringResource(R.string.common_back)) }
                    InstanceDetails(selected, state, canManage, model, section, onRecords) { message, action -> confirmation = message to action }
                }
            }
        } }
        if (section == "instances" && state.candidates.isNotEmpty()) {
            Text(stringResource(R.string.nginx_candidates), style = MaterialTheme.typography.titleSmall)
            state.candidates.forEach { candidate ->
                Text(candidate.executablePath, style = MaterialTheme.typography.bodySmall)
                Text(candidate.configurationPath ?: stringResource(R.string.nginx_configuration_missing), style = MaterialTheme.typography.bodySmall)
                Text(candidate.version ?: stringResource(R.string.websites_version_unknown))
                if (canManage) OutlinedButton(enabled = !state.busy && state.pending.none { it.target == candidate.id } && !model.mutationsBlocked,
                    onClick = { confirmation = R.string.nginx_integrate_confirm to { model.integrate(candidate) } }) {
                    Text(stringResource(R.string.nginx_integrate))
                }
            }
        }
        }
        WorkspaceSection(section == "records") {
        TextButton(onClick = { recoveryDialog = true }, enabled = !state.busy) { Text(stringResource(R.string.nginx_recover)) }
        WebSiteRecovery(state, model) { message, action -> confirmation = message to action }
        state.pending.forEach { pending ->
            Text(stringResource(R.string.nginx_pending, pending.target), style = MaterialTheme.typography.bodySmall)
            if (canManage) TextButton(enabled = !state.busy && !state.loading && state.installation?.state?.active != true && state.operation?.state?.active != true, onClick = { confirmation = R.string.nginx_retry_confirm to { model.resume(pending) } }) {
                ActionLabel(R.string.common_retry)
            }
            if (canManage && pending.action == "integrate" && pending.operationId == null &&
                state.servers.any { it.id == pending.target } && state.tests[pending.target]?.valid == true) {
                TextButton(enabled = !model.mutationsBlocked, onClick = {
                    confirmation = R.string.nginx_accept_facts_confirm to { model.acceptIntegrationFacts(pending) }
                }) { Text(stringResource(R.string.nginx_accept_facts)) }
            }
        }
        if (model.hasIntent && canManage) TextButton(enabled = !state.busy, onClick = model::retryInstallation) {
            ActionLabel(R.string.common_retry)
        }
        if (state.pendingInstallation) Text(stringResource(R.string.nginx_pending_installation), style = MaterialTheme.typography.bodySmall)
        state.operation?.let { operation ->
            ManagementCard {
            Text(stringResource(R.string.nginx_operation, operation.operationId, nginxOperationStateLabel(operation.state)))
            if (operation.problemCode.isNotBlank()) Text(nginxProblemLabel(operation.problemCode), color = MaterialTheme.colorScheme.error)
            operation.snapshotId?.let { Text(stringResource(R.string.nginx_snapshot, it), style = MaterialTheme.typography.bodySmall) }
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollWeb) { ActionLabel(R.string.common_refresh) }
            })
            if (operation.state.active && canManage) TextButton(enabled = !state.busy, onClick = {
                confirmation = R.string.operations_cancel_explanation to model::cancelWeb
            }) { Text(stringResource(R.string.operations_request_cancel)) }
            }
        }
        state.installation?.let { operation ->
            ManagementCard {
            Text(stringResource(R.string.nginx_operation, operation.operationId, installationStateLabel(operation.state)))
            Text(installationStageLabel(operation.stage))
            operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
            operation.problemCode?.let { Text(nginxProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollInstallation) { ActionLabel(R.string.common_refresh) }
            })
            if (operation.state.active && operation.cancellable && canManage) TextButton(enabled = !state.busy, onClick = {
                confirmation = R.string.operations_cancel_explanation to model::cancelInstallation
            }) { Text(stringResource(R.string.operations_request_cancel)) }
            }
        }
        }
    }
    confirmation?.let { (message, action) ->
        AlertDialog(onDismissRequest = { confirmation = null }, title = { Text(stringResource(R.string.nginx_confirm)) },
            text = { Text(stringResource(message)) }, confirmButton = { Button(onClick = { confirmation = null; action() }) { Text(stringResource(R.string.nginx_confirm)) } },
            dismissButton = { TextButton(onClick = { confirmation = null }) { Text(stringResource(R.string.common_cancel)) } })
    }
    WebSiteEditor(state, model)
    if (installDialog) NginxInstallDialog(model, onSubmitted = { installDialog = false; onRecords() }) { installDialog = false }
    if (recoveryDialog) {
        var id by remember { mutableStateOf("") }
        AlertDialog(onDismissRequest = { recoveryDialog = false }, title = { Text(stringResource(R.string.nginx_recover)) },
            text = { OutlinedTextField(id, { id = it }, label = { Text(stringResource(R.string.nginx_operation_id)) }) },
            confirmButton = { Button(enabled = runCatching { InstallationRoutes.operation(id.trim()) }.isSuccess,
                onClick = { recoveryDialog = false; model.recover(id) }) { Text(stringResource(R.string.nginx_confirm)) } },
            dismissButton = { TextButton(onClick = { recoveryDialog = false }) { Text(stringResource(R.string.common_cancel)) } })
    }
}

@Composable
private fun InstanceList(state: NginxState, onSelect: (String) -> Unit) {
    state.servers.forEach { server ->
        OutlinedCard(onClick = { onSelect(server.id) }, modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text("Nginx ${server.version ?: "—"}", style = MaterialTheme.typography.titleMedium)
                Text(server.id, style = MaterialTheme.typography.bodySmall)
                Text(server.executablePath, style = MaterialTheme.typography.bodySmall)
                Text(stringResource(if (server.managementMode == "managed") R.string.nginx_managed else R.string.nginx_integrated))
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun InstanceDetails(server: WebServer, state: NginxState, canManage: Boolean, model: NginxViewModel, section: String, onRecords: () -> Unit,
    confirm: (Int, () -> Unit) -> Unit) {
    Text(stringResource(if (server.managementMode == "managed") R.string.nginx_managed else R.string.nginx_integrated))
    Text(server.executablePath, style = MaterialTheme.typography.bodySmall)
    Text(server.configurationPath ?: stringResource(R.string.nginx_configuration_missing), style = MaterialTheme.typography.bodySmall)
    val status = state.statuses[server.id]
    ExecutionStatusChip(stringResource(when (status?.runtimeState) {
        "running" -> R.string.websites_runtime_running
        "stopped" -> R.string.nginx_stopped
        else -> R.string.websites_runtime_unknown
    }), status?.runtimeState, task = false)
    status?.problemCode?.takeIf(String::isNotBlank)?.let { Text(nginxProblemLabel(it)) }
    val test = state.tests[server.id]
    Text(stringResource(when { !server.canTestConfiguration -> R.string.websites_config_unsupported
        test == null -> R.string.websites_config_unknown
        test.valid -> R.string.websites_config_valid
        else -> R.string.websites_config_invalid }))
    test?.problemCode?.takeIf(String::isNotBlank)?.let { Text(nginxProblemLabel(it)) }
WorkspaceSection(section == "instances") {
    if (canManage) {
        val blocker = when {
            state.busy || state.loading -> R.string.nginx_controls_refreshing
            state.installation?.state?.active == true -> R.string.nginx_controls_installation_running
            state.operation?.state?.active == true -> R.string.nginx_controls_operation_running
            state.pending.any { it.target == server.id } || state.pendingInstallation || model.hasIntent -> R.string.nginx_controls_pending
            state.uncertain -> R.string.nginx_controls_unverified
            else -> null
        }
        blocker?.let { Text(stringResource(it), style = MaterialTheme.typography.bodySmall) }
    }
    if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        WebServerAction.entries.filter { it.supported(server) }.forEach { action ->
            OutlinedButton(enabled = !model.mutationsBlocked && state.pending.none { it.target == server.id },
                onClick = { confirm(if (action == WebServerAction.EnableAcmeHttp01) R.string.nginx_acme_confirm else R.string.nginx_lifecycle_confirm) { model.lifecycle(server, action) } }) {
                Text(nginxActionLabel(action))
            }
        }
        if (server.canUninstall) OutlinedButton(enabled = !state.busy && !state.loading && state.operation?.state?.active != true && state.pending.none { it.target == server.id } && state.installation?.state?.active != true && !state.pendingInstallation && !model.hasIntent,
            onClick = { confirm(R.string.nginx_uninstall_confirm) { model.uninstall(server); onRecords() } }) { Text(stringResource(R.string.nginx_uninstall)) }
    }}

}

@Composable
private fun NginxInstallDialog(model: NginxViewModel, onSubmitted: () -> Unit, dismiss: () -> Unit) {
    val container = appContainer()
    val owner = remember { container.activeSession }
    val state = model.state
    var version by remember { mutableStateOf(model.intentVersion.orEmpty()) }
    var source by remember { mutableStateOf(if (model.intentUsesPackage) InstallationPackageSource.ServerFile else InstallationPackageSource.HostDownload) }
    var remotePath by remember { mutableStateOf("") }
    var confirmed by remember { mutableStateOf(false) }
    val picker = rememberLauncherForActivityResult(rememberUsageOpenDocument("NginxManager.archive")) { uri ->
        if (uri != null && container.activeSession === owner && !model.hasIntent) model.upload(uri)
    }
    val windows = state.system in NginxViewModel.windowsSystems
    val locked = state.busy || model.hasIntent
    LaunchedEffect(state.catalog) {
        if (!model.hasIntent && version.isBlank()) {
            version = state.catalog?.stableVersion ?: state.catalog?.mainlineVersion ?: state.catalog?.versions?.firstOrNull().orEmpty()
        }
    }
    val initialOperationId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId) {
        if (state.installation != null && state.installation.operationId != initialOperationId) onSubmitted()
    }
    AlertDialog(onDismissRequest = dismiss, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.nginx_install)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(if (windows) R.string.nginx_windows_install else R.string.nginx_ubuntu_install))
            if (windows) {
                state.catalog?.versions.orEmpty().forEach { item ->
                    TextButton(enabled = !locked, onClick = { version = item; model.clearReference() }) { Text(item) }
                }
                OutlinedTextField(version, { version = it; model.clearReference() }, enabled = !locked, modifier = Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.nginx_version)) }, singleLine = true)
                ManualPackageDownload(version.trim(), !locked, state.busy,
                    (state.download as? ApiResult.Success)?.value?.takeIf { it.version == version.trim() }?.url,
                    onRequest = { model.download(version.trim()) })
                InstallationPackagePicker(source, !locked, { source = it; model.clearReference() },
                    remotePath, { remotePath = it; model.clearReference() }, { model.fileReference(remotePath) },
                    { picker.launch(arrayOf("application/zip", "application/octet-stream")) }, state.reference)
            }
            RefreshProgressIndicator(visible = state.busy)
            state.uploadBytes?.let { Text(stringResource(R.string.nginx_upload_bytes, it)) }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(confirmed, { confirmed = it }, enabled = !state.busy)
                Text(stringResource(R.string.nginx_install_confirm))
            }
        } },
        confirmButton = { Button(enabled = !state.busy && confirmed && (model.hasIntent || (!state.pendingInstallation &&
            (!windows || (version.trim().matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+")) && (source == InstallationPackageSource.HostDownload || state.reference?.expired() == false))))),
            onClick = { model.install(if (windows) version.trim() else null, windows && source != InstallationPackageSource.HostDownload) }) {
            Text(stringResource(if (model.hasIntent) R.string.common_retry else R.string.nginx_install))
        } }, dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_close)) } })
}

@Composable
internal fun nginxActionLabel(action: WebServerAction): String = stringResource(when (action) {
    WebServerAction.Start -> R.string.nginx_start
    WebServerAction.Stop -> R.string.nginx_stop
    WebServerAction.Restart -> R.string.nginx_restart
    WebServerAction.Reload -> R.string.nginx_reload
    WebServerAction.EnableAcmeHttp01 -> R.string.nginx_acme
})
@Composable
internal fun nginxOperationStateLabel(state: WebServerOperationState): String = stringResource(when (state) {
    WebServerOperationState.Queued -> R.string.operations_running
    WebServerOperationState.Running -> R.string.operations_running
    WebServerOperationState.Succeeded -> R.string.operations_succeeded
    WebServerOperationState.Failed -> R.string.operations_failed
    WebServerOperationState.Cancelled -> R.string.operations_cancelled
})
@Composable
internal fun nginxProblemLabel(code: String): String = when {
    code.startsWith("installation.") -> installationProblemLabel(code)
    else -> stringResource(when (code) {
        "webserver.site_certificate_not_usable", "webserver.site_certificate_unverified" -> R.string.certificates_binding_unverified
        "webserver.site_certificate_domain_mismatch" -> R.string.certificates_binding_domain
        "elevation-required", "webserver.elevation_required", "webserver.config_elevation_required", "webserver.install_elevation_required" -> R.string.error_elevation_required
        "webserver.site_changed" -> R.string.websites_site_changed
        "webserver.site_already_exists", "webserver.site_binding_conflict", "webserver.site_conflict" -> R.string.websites_site_conflict
        "webserver.site_config_test_failed", "webserver.site_upstream_unresolvable" -> R.string.websites_site_config_failed
        "webserver.site_acl_package_required" -> R.string.websites_site_acl_package_required
        "webserver.site_save_failed", "webserver.site_delete_failed", "webserver.site_reload_failed", "webserver.site_permission_grant_failed" -> R.string.websites_site_apply_failed
        "webserver.site_name_invalid", "webserver.site_server_name_required", "webserver.site_port_invalid", "webserver.site_server_name_invalid",
        "webserver.site_certificate_required", "webserver.site_certificate_file_invalid", "webserver.site_root_invalid", "webserver.site_route_path_invalid",
        "webserver.site_upstream_invalid", "webserver.site_content_required", "webserver.site_https_redirect_invalid" -> R.string.websites_site_validation
        "webserver.version_catalog_unavailable" -> R.string.nginx_catalog_failed
        "webserver.package_invalid", "webserver.version_invalid", "webserver.install_layout_invalid" -> R.string.nginx_package_invalid
        "webserver.managed_already_installed", "webserver.system_nginx_already_installed", "webserver.ownership_conflict", "webserver.configuration_changed" -> R.string.nginx_conflict
        "webserver.config_test_failed", "webserver.include_context_not_supported", "webserver.configuration_not_found" -> R.string.nginx_config_failed
        "webserver.managed_required", "webserver.reload_not_permitted", "webserver.install_unsupported_platform", "webserver.acme_integration_required" -> R.string.nginx_unsupported
        "webserver.privileged_helper_unavailable" -> R.string.nginx_helper_failed
        "webserver.install_failed", "webserver.uninstall_failed" -> R.string.installation_problem_failed
        "webserver.operation_cancelled" -> R.string.operations_cancelled
        "webserver.acme_no_managed_sites" -> R.string.nginx_acme_no_sites
        else -> R.string.error_generic
    })
}
