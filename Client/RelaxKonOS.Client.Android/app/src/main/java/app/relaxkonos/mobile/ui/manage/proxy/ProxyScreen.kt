package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

private data class ProxyConfirmation(val action: () -> Unit)
@OptIn(ExperimentalLayoutApi::class)
@Composable fun ProxyScreen(onBack: () -> Unit, initialOperationId: String? = null, modifier: Modifier = Modifier) {
    val model: ProxyViewModel = viewModel(); val state = model.state; val owner = appContainer().activeSession
    val available = owner?.capabilities?.contains(ServerCapabilities.PROXY) == true
    val canManage = available && owner?.privilegedOperations == true
    val epoch = model.sessionEpoch
    var confirm by remember(owner, epoch) { mutableStateOf<ProxyConfirmation?>(null) }
    var install by remember(owner, epoch) { mutableStateOf(false) }
    var editor by remember(owner, epoch) { mutableStateOf<String?>(null) }
    var selected by remember(owner, epoch) { mutableStateOf<String?>(null) }
    var recoveredId by remember(owner, epoch) { mutableStateOf("") }
    var recoverInstallation by remember(owner, epoch) { mutableStateOf(false) }
    var identified by remember(owner, epoch) { mutableStateOf(false) }
    var recovering by remember(owner, epoch) { mutableStateOf(false) }
    var attemptedInitial by remember(owner, epoch, initialOperationId) { mutableStateOf(false) }
    var section by rememberSaveable(owner, epoch) { mutableStateOf("overview") }
    val networkSection = section in setOf("connections", "logs", "settings")
    var showRecovery by remember(owner, epoch) { mutableStateOf(false) }
    OperationMessageDialog(if (state.busy) null else state.problemCode?.let { proxyProblemLabel(it) } ?: if (state.uncertain) stringResource(R.string.mihomo_uncertain) else null, tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger)
    LaunchedEffect(owner, epoch) { if (available) model.refresh() }
    LaunchedEffect(owner, epoch, initialOperationId, state.busy) {
        if (available && initialOperationId != null && !state.busy && !attemptedInitial) { attemptedInitial = true; section = "records"; model.recoverOperation(initialOperationId) }
    }
    LaunchedEffect(owner, epoch, state.operation?.operationId, state.operation?.state, state.busy, state.operationVerified) {
        model.observeOperation()
    }
    LaunchedEffect(owner, epoch, state.installation?.operationId, state.installation?.state, state.busy, state.installationVerified) {
        model.observeInstallation()
    }
    val overview = (state.overview as? ApiResult.Success)?.value
    val notInstalled = overview?.runtime?.state == ProxyRuntimeState.NotInstalled
    val profiles = (state.profiles as? ApiResult.Success)?.value
    val subscriptions = (state.subscriptions as? ApiResult.Success)?.value
    val current = profiles?.firstOrNull { it.id == selected }
    val ready = !state.busy && !state.uncertain && overview != null && profiles != null && subscriptions != null && state.pending.isEmpty() &&
        (state.operation == null || state.operationVerified && !state.operation.state.active) &&
        (state.installation == null || state.installationVerified && !state.installation.state.active) && !state.pendingInstallation
    val connected = overview?.controllerReachable == true
    LaunchedEffect(owner, epoch, section, connected, state.uncertain) {
        model.observeDiagnostics(section)
    }
    BackHandler(section == "profiles" && selected != null && editor == null && !state.busy) { selected = null }
    // Retained sections own their item spacing; hidden sections must not add root gaps.
    WorkspaceColumn(stringResource(R.string.mihomo_title), onBack, listOf(WorkspaceDestination("overview", R.string.workspace_overview), WorkspaceDestination("profiles", R.string.workspace_profiles), WorkspaceDestination("nodes", R.string.workspace_nodes), WorkspaceDestination("connections", R.string.workspace_connections), WorkspaceDestination("logs", R.string.workspace_logs), WorkspaceDestination("settings", R.string.workspace_settings), WorkspaceDestination("records", R.string.workspace_records)), section, { section = it }, modifier, stateKey = owner to epoch, contentSpacing = 0.dp, contentPadding = PaddingValues(horizontal = Spacing.lg, vertical = Spacing.sm)) {
        if (!available) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
WorkspaceSection(section == "overview") {
        ManagementCard {
        Text(stringResource(R.string.mihomo_intro))
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = model::refresh) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            if (canManage) OutlinedButton(enabled = !state.busy && state.installation?.state?.active != true, onClick = { install = true }) { Text(if (notInstalled) stringResource(R.string.runtime_install_action, "Mihomo") else stringResource(R.string.mihomo_runtime_manage)) }
        })
        RefreshProgressIndicator(visible = state.busy)

        state.observedAtMillis?.let { Text(stringResource(R.string.mihomo_observed, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall) }
        if (overview == null) Text(stringResource(R.string.mihomo_unavailable)) else {
            if (notInstalled) Text(stringResource(R.string.runtime_install_hint, "Mihomo")) else {
            ExecutionStatusChip(proxyRuntimeLabel(overview.runtime.state), overview.runtime.state.wire, task = false)
            overview.runtime.version?.let { Text(stringResource(R.string.tunnels_version_value, it)) }
            overview.runtime.previousVersion?.let { Text(stringResource(R.string.tunnels_previous_version, it)) }
            Text(stringResource(if (overview.runtime.integrityVerified) R.string.tunnels_integrity_verified else R.string.tunnels_integrity_unverified))
            overview.runtime.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            overview.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ProxyAction.entries.filter { it in setOf(ProxyAction.Start, ProxyAction.Stop, ProxyAction.Restart) }.forEach { action ->
                    val supported = overview.runtime.state in setOf(ProxyRuntimeState.Stopped, ProxyRuntimeState.Running, ProxyRuntimeState.Degraded, ProxyRuntimeState.Failed)
                    OutlinedButton(enabled = ready && supported, onClick = { confirm = ProxyConfirmation { model.queue(action) } }) { Text(proxyActionLabel(action)) }
                }
            }
            }
        }
        }
}
        if (section != "records" && (state.pending.isNotEmpty() || state.pendingInstallation || state.uncertain || state.operation?.state?.active == true || state.installation?.state?.active == true)) TextButton(modifier = Modifier.padding(vertical = Spacing.sm), onClick = { section = "records" }) { Text(stringResource(R.string.workspace_records_attention)) }
        WorkspaceSection(section == "records") {
        if (state.operation == null && state.installation == null && state.pending.isEmpty() && !state.pendingInstallation) ManagementCard { Text(stringResource(R.string.workspace_records_empty)) }
                state.operation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            ExecutionStatusChip(if (state.operationVerified) proxyOperationLabel(operation.state) else stringResource(R.string.mihomo_unverified), if (state.operationVerified) operation.state.wire else null)
            if (state.operationVerified) { Text(proxyStageLabel(operation.stage)); operation.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) } }
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollOperation) { ActionLabel(R.string.common_refresh) }
            })
            }
        }
        if (notInstalled) TextButton(onClick = { showRecovery = !showRecovery }) { Text(stringResource(R.string.runtime_recovery_tools)) }
        if (!notInstalled || showRecovery) TextButton(enabled = !state.busy, onClick = { recoveredId = ""; recoverInstallation = false; recovering = true }) { Text(stringResource(R.string.mihomo_recover_operation)) }
        state.installation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                Text(installationKindLabel(operation.kind) + " · " + installationStateLabel(operation.state)); Text(installationStageLabel(operation.stage))
                operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            } else Text(stringResource(R.string.mihomo_unverified))
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollInstall) { ActionLabel(R.string.common_refresh) }
            })
            if (canManage && state.installationVerified && operation.state.active && operation.cancellable) TextButton(enabled = !state.busy,
                onClick = { confirm = ProxyConfirmation(model::cancelInstall) }) { Text(stringResource(R.string.common_cancel)) }
            }
        }
        if (state.pendingInstallation) {
            Text(stringResource(R.string.mihomo_install_pending), color = MaterialTheme.colorScheme.error)
            Text(stringResource(R.string.mihomo_install_accept_note), style = MaterialTheme.typography.bodySmall)
            if (canManage) TextButton(enabled = !state.busy, onClick = { confirm = ProxyConfirmation(model::acceptInstallationFacts) }) {
                Text(stringResource(R.string.mihomo_accept_facts))
            }
            if (canManage && model.hasIntent) TextButton(enabled = !state.busy, onClick = { confirm = ProxyConfirmation(model::retryInstall) }) { ActionLabel(R.string.common_retry) }
        }
        if (canManage && (!notInstalled || showRecovery)) TextButton(enabled = !state.busy, onClick = { recoveredId = if (state.installationVerified) state.installation?.operationId.orEmpty() else ""; identified = false; recoverInstallation = true; recovering = true }) { Text(stringResource(R.string.mihomo_recover_installation)) }
        state.pending.forEach { pending ->
            ManagementCard {
            Text(stringResource(R.string.mihomo_pending), color = MaterialTheme.colorScheme.error)
            Text(pending.key, style = MaterialTheme.typography.bodySmall)
            if (pending.action != null) {
                Text(proxyActionLabel(pending.action))
                if (canManage) TextButton(enabled = !state.busy, onClick = { confirm = ProxyConfirmation { model.resume(pending) } }) { Text(stringResource(R.string.mihomo_replay)) }
            } else {
                Text(stringResource(R.string.mihomo_pending_sync))
                TextButton(enabled = !state.busy && overview != null && profiles != null && subscriptions != null,
                    onClick = { confirm = ProxyConfirmation { model.accept(pending) } }) { Text(stringResource(R.string.mihomo_accept_facts)) }
            }
            }
        }
        }
        WorkspaceSection(networkSection) {
            if (!notInstalled) key(owner, epoch) { ProxyNetworkPanel(model, canManage, ready, section) { action -> confirm = ProxyConfirmation(action) } }
        }
WorkspaceSection(section == "profiles") {
        Text(stringResource(R.string.mihomo_subscriptions), style = MaterialTheme.typography.titleMedium)
        if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(enabled = ready, onClick = { editor = "subscription" }) { Text(stringResource(R.string.mihomo_import)) }
            TextButton(enabled = ready && !subscriptions.isNullOrEmpty(), onClick = { confirm = ProxyConfirmation { model.queue(ProxyAction.RefreshAll) } }) { Text(stringResource(R.string.mihomo_refresh_all)) }
        }
        if (subscriptions == null) Text(stringResource(R.string.mihomo_unavailable)) else subscriptions.forEach { subscription ->
            ManagementCard {
            Text(subscription.name + if (subscription.active) " · " + stringResource(R.string.mihomo_active) else "")
            subscription.lastUpdatedAtMillis?.let { Text(DateFormat.getDateTimeInstance().format(Date(it)), style = MaterialTheme.typography.bodySmall) }
            if (canManage) PageActionRow(refresh = {
                    TextButton(enabled = ready, onClick = { confirm = ProxyConfirmation { model.queue(ProxyAction.RefreshSubscription, subscription.id) } }) { ActionLabel(R.string.common_refresh) }
            }, actions = {
                TextButton(enabled = ready, onClick = { confirm = ProxyConfirmation { model.queue(ProxyAction.ActivateSubscription, subscription.id) } }) { Text(stringResource(R.string.mihomo_activate)) }
            })
            }
        }
        HorizontalDivider()
        Text(stringResource(R.string.mihomo_profiles), style = MaterialTheme.typography.titleMedium)
        if (canManage) OutlinedButton(enabled = ready, onClick = { selected = null; editor = "profile" }) { Text(stringResource(R.string.mihomo_create_profile)) }
        if (profiles == null) Text(stringResource(R.string.mihomo_unavailable)) else BoxWithProfileList(profiles, selected, { selected = it }) {
            current?.let { profile ->
                ManagementCard {
                Text(profile.name, style = MaterialTheme.typography.titleSmall)
                Text(stringResource(R.string.mihomo_revision, profile.revision))
                if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    TextButton(enabled = ready, onClick = { editor = "profile" }) { Text(stringResource(R.string.tunnels_edit)) }
                    TextButton(enabled = ready && !profile.active, onClick = { confirm = ProxyConfirmation { model.activate(profile) } }) { Text(stringResource(R.string.mihomo_activate)) }
                    TextButton(enabled = ready && overview?.supportsValidation == true, onClick = { editor = "yaml" }) { Text(stringResource(R.string.mihomo_apply_yaml)) }
                    TextButton(enabled = ready && !profile.active, onClick = { confirm = ProxyConfirmation { model.delete(profile) } }) { ActionLabel(R.string.common_delete) }
                }
                }
            }
        }
}
        WorkspaceSection(section == "nodes") {
        if (!notInstalled) {
        if (!connected) Text(stringResource(R.string.mihomo_controller_unavailable)) else {
            val routing = (state.routing as? ApiResult.Success)?.value
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) { ProxyRoutingMode.entries.forEach { mode ->
                FilterChip(selected = routing == mode, enabled = canManage && ready && routing != null,
                    onClick = { confirm = ProxyConfirmation { model.routing(mode) } }, label = { Text(proxyRoutingLabel(mode)) })
            } }
            key(owner, epoch) { ProxyNodesPanel(state, canManage, ready, model::refresh, model::select, model::testGroup) }
        }
        }
}
            }
    if (install) key(owner, epoch) { ProxyInstallEditor(model, onSubmitted = { section = "records" }) { install = false } }
    editor?.let { kind -> key(owner, epoch, kind, current?.id) { ProxyEditor(kind, current, model) { editor = null } } }
    confirm?.let { request -> AlertDialog(onDismissRequest = { confirm = null }, title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Text(stringResource(R.string.mihomo_change_confirm)) }, confirmButton = { Button(enabled = !state.busy, onClick = { confirm = null; request.action() }) { Text(stringResource(R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(onClick = { confirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (recovering) AlertDialog(onDismissRequest = { recovering = false }, title = { Text(stringResource(if (recoverInstallation) R.string.mihomo_recover_installation else R.string.mihomo_recover_operation)) },
        text = { Column { Text(stringResource(R.string.mihomo_recover_note)); OutlinedTextField(recoveredId, { recoveredId = it }, singleLine = true, label = { Text(stringResource(R.string.certificates_operation_id)) })
            if (recoverInstallation && state.pendingInstallation) ProxyCheck(identified, !state.busy, R.string.mihomo_identify) { identified = it }
        } }, confirmButton = { Button(enabled = !state.busy && runCatching { InstallationRoutes.canonicalId(recoveredId.trim()) }.isSuccess && (!recoverInstallation || !state.pendingInstallation || identified), onClick = {
            if (recoverInstallation) model.recoverInstall(recoveredId, identified) else model.recoverOperation(recoveredId); recovering = false
        }) { ActionLabel(R.string.common_refresh) } }, dismissButton = { TextButton(onClick = { recovering = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable private fun BoxWithProfileList(profiles: List<ProxyProfile>, selected: String?, select: (String?) -> Unit, detail: @Composable () -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val list: @Composable () -> Unit = { Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) { profiles.forEach { profile -> OutlinedCard(onClick = { select(profile.id) }, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(profile.name, style = MaterialTheme.typography.titleMedium)
            if (profile.active) Text(stringResource(R.string.mihomo_active), color = MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.mihomo_revision, profile.revision), style = MaterialTheme.typography.bodySmall)
        } } } } }
        if (maxWidth >= 600.dp) Row { Column(Modifier.weight(1f)) { list() }; Column(Modifier.weight(2f)) { detail() } }
        else Column { if (selected == null) list() else { TextButton(onClick = { select(null) }) { Text(stringResource(R.string.common_back)) }; detail() } }
    }
}

@Composable private fun ProxyEditor(kind: String, profile: ProxyProfile?, model: ProxyViewModel, dismiss: () -> Unit) {
    var name by remember { mutableStateOf(if (kind == "profile") profile?.name.orEmpty() else "") }
    var content by remember { mutableStateOf("") }; var route by remember { mutableStateOf(ProxyDownloadRoute.Direct) }
    var confirmed by remember { mutableStateOf(false) }; var discard by remember { mutableStateOf(false) }
    val initial = remember { model.state.savedEpoch }; val state = model.state
    LaunchedEffect(state.savedEpoch) { if (state.savedEpoch != initial) dismiss() }
    val close = { if (content.isNotEmpty() || name != (if (kind == "profile") profile?.name.orEmpty() else "")) discard = true else dismiss() }
    AlertDialog(onDismissRequest = { if (!state.busy) close() }, modifier = Modifier.imePadding(), title = { Text(stringResource(when (kind) { "yaml" -> R.string.mihomo_apply_yaml; "subscription" -> R.string.mihomo_import; else -> R.string.mihomo_profiles })) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            if (kind != "yaml") ProxyText(name, !state.busy, R.string.mihomo_name) { name = it }
            if (kind == "yaml") { Text(stringResource(R.string.mihomo_yaml_note)); OutlinedTextField(content, { if (it.length <= 1048576) content = it }, enabled = !state.busy, minLines = 6, label = { Text(stringResource(R.string.mihomo_yaml)) }) }
            if (kind == "subscription") {
                Text(stringResource(R.string.mihomo_subscription_note))
                OutlinedTextField(content, { content = it }, enabled = !state.busy, singleLine = true, visualTransformation = PasswordVisualTransformation(), label = { Text(stringResource(R.string.mihomo_source_url)) })
                ProxyDownloadRoute.entries.forEach { option -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { RadioButton(route == option, { route = option }, enabled = !state.busy && (option == ProxyDownloadRoute.Direct || state.downloadOptions)); Text(stringResource(if (option == ProxyDownloadRoute.Direct) R.string.mihomo_download_direct else R.string.mihomo_download_system)) } }
            }
            if (kind != "profile") ProxyCheck(confirmed, !state.busy, R.string.mihomo_apply_confirm) { confirmed = it }

        } }, confirmButton = { Button(enabled = !state.busy && state.pending.isEmpty() && name.length <= 128 && when (kind) {
            "profile" -> name.isNotBlank(); "yaml" -> confirmed && content.isNotBlank(); else -> confirmed && content.length <= 16384 && runCatching { java.net.URI(content.trim()).let { it.scheme in setOf("https", "http") && it.host != null && it.userInfo == null } }.getOrDefault(false)
        }, onClick = { when (kind) { "profile" -> model.saveProfile(profile, name); "yaml" -> model.apply(requireNotNull(profile), content); else -> model.import(content, name, route) } }) { ActionLabel(R.string.common_save) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = close) { Text(stringResource(R.string.common_cancel)) } })
    if (discard) AlertDialog(onDismissRequest = { discard = false }, text = { Text(stringResource(R.string.mihomo_discard)) }, confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_close)) } }, dismissButton = { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } })
}
@Composable internal fun ProxyText(value: String, enabled: Boolean, label: Int, change: (String) -> Unit) { OutlinedTextField(value, change, enabled = enabled, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(label)) }) }
@Composable internal fun ProxyCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) { Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(value, change, enabled = enabled); Text(stringResource(label)) } }
