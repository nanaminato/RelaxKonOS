package app.relaxkonos.mobile.ui.manage.docker

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
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay
import java.text.DateFormat
import java.util.Date

private data class MirrorDraft(val id: String? = null, val name: String = "", val endpoint: String = "") {
    fun request() = DockerMirrorRequest(name.trim(), endpoint.trim())
}
private data class ControlConfirmation(val facts: DockerControlFacts, val change: DockerControlChange)

@OptIn(ExperimentalLayoutApi::class)
@Composable fun DockerControlScreen(onBack: () -> Unit, modifier: Modifier = Modifier, mirrorsOnly: Boolean = false, recordsOnly: Boolean = false, active: Boolean = true) {
    val model: DockerControlViewModel = viewModel(); val owner = appContainer().activeSession; val state = model.state
    val visible = state.owner === owner; val facts = state.facts.takeIf { visible }
    val manage = visible && owner?.privilegedOperations == true
    val ready = manage && !state.busy && state.pending.isEmpty() && !state.resourcePending && !state.pendingInstallation &&
        state.installation?.state?.active != true && (state.installation == null || state.installationVerified) && facts != null
    var draft by remember(owner) { mutableStateOf<MirrorDraft?>(null) }
    var confirmation by remember(owner) { mutableStateOf<ControlConfirmation?>(null) }
    var leave by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    var install by remember(owner) { mutableStateOf(false) }; var cancel by remember(owner) { mutableStateOf(false) }
    var recover by remember(owner) { mutableStateOf(false) }; var id by remember(owner) { mutableStateOf("") }; var identified by remember(owner) { mutableStateOf(false) }
    val navigate: (() -> Unit) -> Unit = { action -> if (draft != null) leave = action else action() }
    LaunchedEffect(owner, state.owner) { if (visible && owner != null) model.refresh() }
    LaunchedEffect(owner, state.saved) { if (visible && state.saved > 0) draft = null }
    LaunchedEffect(owner, state.installation?.operationId, state.installation?.state, state.installationVerified, state.busy) {
        if (visible && state.installationVerified && state.installation?.state?.active == true && !state.busy) { delay(1500); model.pollInstall() }
    }
    DisposableEffect(owner) { onDispose { model.stop() } }
    BackHandler(active && draft != null) { navigate { draft = null } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(if (recordsOnly) R.string.workspace_records else if (mirrorsOnly) R.string.workspace_mirrors else R.string.docker_title), onBack = { navigate(onBack) })
        if (owner?.capabilities?.contains(ServerCapabilities.DOCKER) != true) { Text(stringResource(R.string.error_capability_missing)); return@Column }
        TextButton(enabled = !state.busy, onClick = { navigate { draft = null; model.refresh() } }) { Text(stringResource(R.string.common_refresh)) }
        if (visible && state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        OperationMessageDialog(state.problem?.takeIf { visible && !state.busy }?.let { controlProblem(it) })
        if (visible && recordsOnly) state.pending.forEach { pending ->
            ManagementCard {
            Text(stringResource(R.string.docker_control_pending), color = MaterialTheme.colorScheme.error)
            Text(controlActionLabel(pending.kind))
            OutlinedButton(enabled = !state.busy, onClick = { model.accept(pending) }) { Text(stringResource(R.string.docker_control_accept)) }
            }
        }
        if (visible && state.resourcePending) Text(stringResource(R.string.docker_resources_pending), color = MaterialTheme.colorScheme.error)
WorkspaceSection(!mirrorsOnly && !recordsOnly) {
        ManagementCard {
        Text(stringResource(R.string.docker_runtime), style = MaterialTheme.typography.titleLarge)
        if (facts == null) Text(stringResource(R.string.docker_control_unverified)) else {
            val notInstalled = facts.status.problemCode == "docker.not_installed"
            Text(stringResource(if (notInstalled) R.string.docker_control_not_installed else if (facts.status.available) R.string.docker_control_running else R.string.docker_control_unavailable))
            if (facts.status.serverVersion.isNotEmpty()) Text(facts.status.serverVersion)
            Text(listOf(facts.status.operatingSystem, facts.status.architecture).filter(String::isNotBlank).joinToString(" / "))
            if (!notInstalled && facts.status.problemCode.isNotBlank()) Text(controlProblem(facts.status.problemCode))
            state.checkedAtMillis?.let { Text(stringResource(R.string.operations_checked, DateFormat.getDateTimeInstance().format(Date(it)))) }
            if (owner.serverPlatform.equals("linux", true)) Text(stringResource(R.string.docker_control_linux))
            else Text(stringResource(R.string.docker_control_windows))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                if (manage && !notInstalled) DockerEngineAction.entries.forEach { action ->
                    val kind = when (action) { DockerEngineAction.Start -> DockerControlKind.EngineStart; DockerEngineAction.Stop -> DockerControlKind.EngineStop; DockerEngineAction.Restart -> DockerControlKind.EngineRestart }
                    OutlinedButton(enabled = ready && draft == null && (action == DockerEngineAction.Start || facts.status.available),
                        onClick = { confirmation = ControlConfirmation(facts, DockerControlChange(kind)) }) { Text(controlActionLabel(kind)) }
                }
                if (manage && owner.serverPlatform.equals("linux", true) && (!facts.status.available && facts.status.problemCode == "docker.not_installed" || state.pendingInstallation)) {
                    OutlinedButton(enabled = !state.busy && draft == null && state.pending.isEmpty() && state.installation?.state?.active != true &&
                        (state.installation == null || state.installationVerified || state.pendingInstallation), onClick = { install = true }) {
                        Text(stringResource(if (state.pendingInstallation) R.string.common_retry else R.string.docker_control_install))
                    }
                }
            }
        }
        }
}
        WorkspaceSection(recordsOnly) {
        val recordModel: DockerResourceViewModel = viewModel(key = "docker-resources-all")
        val recordState = recordModel.state
        LaunchedEffect(owner, recordState.owner, recordsOnly) { if (recordsOnly && recordState.owner === owner) recordModel.refresh() }
        if (recordState.owner === owner) recordState.pending.forEach { marker -> ManagementCard {
            Text(resourceActionLabel(marker.action)); marker.target?.let { Text(it) }
            TextButton(enabled = !recordState.busy, onClick = recordModel::refresh) { Text(stringResource(R.string.common_refresh)) }
            OutlinedButton(enabled = !recordState.busy && recordState.facts?.status?.available == true, onClick = { recordModel.accept(marker) }) { Text(stringResource(R.string.docker_control_accept)) }
        } }
        DockerResourceKind.entries.forEach { kind ->
            val resourceModel: DockerResourceViewModel = viewModel(key = "docker-resources-${kind.name}")
            val resourceState = resourceModel.state
            if (resourceState.owner === owner) {
                resourceState.result?.let { result -> ManagementCard {
                    var showLog by remember(owner, kind, result) { mutableStateOf(false) }
                    Text(resourceKindLabel(kind), style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(if (resourceState.pending.isNotEmpty()) R.string.docker_control_unverified else if (result.success) R.string.docker_operation_complete else R.string.docker_operation_failed))
                    result.problemCode.takeIf(String::isNotBlank)?.let { Text(controlProblem(it)) }
                    if (result.logLines.isNotEmpty()) TextButton(onClick = { showLog = !showLog }) { Text(stringResource(if (showLog) R.string.docker_resources_hide_sensitive else R.string.docker_resources_show_sensitive)) }
                    if (showLog) result.logLines.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
                    if (result.logTruncated) Text(stringResource(R.string.docker_resources_truncated))
                } }
            }
        }
        if (visible && state.pendingInstallation && manage) OutlinedButton(enabled = !state.busy && state.pending.isEmpty() && state.installation?.state?.active != true && (state.installation == null || state.installationVerified || state.pendingInstallation), onClick = { install = true }) { Text(stringResource(R.string.common_retry)) }
                if (visible && state.pendingInstallation) Text(stringResource(R.string.installation_pending, installationServiceLabel(InstallationService.Docker), installationKindLabel(InstallationKind.Install)), color = MaterialTheme.colorScheme.error)
        if (manage) TextButton(enabled = !state.busy, onClick = { identified = false; recover = true }) { Text(stringResource(R.string.installation_recover)) }
        if (visible) state.installation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                Text(installationStateLabel(operation.state)); Text(installationStageLabel(operation.stage))
                operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.let { Text(installationProblemLabel(it)) }
                if (operation.state.active && operation.cancellable && manage) OutlinedButton(enabled = !state.busy, onClick = { cancel = true }) { Text(stringResource(R.string.operations_request_cancel)) }
            } else Text(stringResource(R.string.docker_control_unverified))
            }
        }
        }
WorkspaceSection(mirrorsOnly) {
        Text(stringResource(R.string.docker_control_mirrors), style = MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.docker_control_mirror_note))
        if (facts != null) {
            if (manage) OutlinedButton(enabled = ready && draft == null, onClick = { draft = MirrorDraft() }) { Text(stringResource(R.string.docker_control_add_mirror)) }
            BoxWithConstraints(Modifier.fillMaxWidth()) {
                val wide = maxWidth >= 600.dp
                Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    if (wide || draft == null) Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        facts.mirrors.forEach { mirror ->
                            ManagementCard {
                            Text(if (mirror.default) stringResource(R.string.docker_control_default_mirror) else mirror.name, style = MaterialTheme.typography.titleMedium)
                            if (!mirror.default) Text(mirror.endpoint)
                            if (mirror.selected) Text(stringResource(R.string.docker_control_selected))
                            if (manage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                TextButton(enabled = ready && !mirror.selected && draft == null, onClick = { confirmation = ControlConfirmation(facts, DockerControlChange(DockerControlKind.MirrorSelect, mirror.id)) }) { Text(stringResource(R.string.docker_control_select)) }
                                if (!mirror.default) {
                                    TextButton(enabled = ready && draft == null, onClick = { draft = MirrorDraft(mirror.id, mirror.name, mirror.endpoint) }) { Text(stringResource(R.string.docker_control_edit_mirror)) }
                                    TextButton(enabled = ready && draft == null, onClick = { confirmation = ControlConfirmation(facts, DockerControlChange(DockerControlKind.MirrorDelete, mirror.id)) }) { Text(stringResource(R.string.common_delete)) }
                                }
                            }
                            }
                        }
                    }
                    draft?.let { editing -> Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text(stringResource(if (editing.id == null) R.string.docker_control_add_mirror else R.string.docker_control_edit_mirror))
                        OutlinedTextField(editing.name, { draft = editing.copy(name = it) }, enabled = ready, singleLine = true, label = { Text(stringResource(R.string.docker_control_mirror_name)) })
                        OutlinedTextField(editing.endpoint, { draft = editing.copy(endpoint = it) }, enabled = ready, singleLine = true, label = { Text(stringResource(R.string.docker_control_mirror_endpoint)) })
                        Text(stringResource(R.string.docker_control_mirror_format))
                        Button(enabled = ready && DockerMirrorValidation.valid(editing.request()), onClick = {
                            confirmation = ControlConfirmation(facts, DockerControlChange(if (editing.id == null) DockerControlKind.MirrorCreate else DockerControlKind.MirrorUpdate, editing.id, editing.request()))
                        }) { Text(stringResource(R.string.common_save)) }
                        TextButton(onClick = { navigate { draft = null } }) { Text(stringResource(R.string.common_cancel)) }
                    } }
                }
            }
        }
}
            }
    confirmation?.let { pending -> AlertDialog(onDismissRequest = { confirmation = null }, title = { Text(controlActionLabel(pending.change.kind)) }, text = { Column {
        if (pending.change.engine != null) Text(stringResource(R.string.docker_control_engine_warning))
        else {
            Text(stringResource(R.string.docker_control_mirror_warning))
            val target = pending.facts.mirrors.firstOrNull { it.id == pending.change.target }
            target?.let { Text(if (it.default) stringResource(R.string.docker_control_default_mirror) else it.name); if (!it.default) Text(it.endpoint) }
            pending.change.mirror?.let { Text(it.name); Text(it.endpoint) }
            if (pending.change.kind == DockerControlKind.MirrorDelete && target?.selected == true) Text(stringResource(R.string.docker_control_delete_selected))
        }
    } }, confirmButton = { Button(enabled = ready, onClick = { confirmation = null; model.change(pending.facts, pending.change) }) { Text(stringResource(R.string.docker_control_confirm)) } },
        dismissButton = { TextButton(onClick = { confirmation = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (leave != null) AlertDialog(onDismissRequest = { leave = null }, title = { Text(stringResource(R.string.docker_control_discard)) },
        text = { Text(stringResource(R.string.docker_control_discard_note)) }, confirmButton = { TextButton(onClick = { val action = leave; leave = null; action?.invoke() }) { Text(stringResource(R.string.docker_control_confirm)) } },
        dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.common_cancel)) } })
    if (install) AlertDialog(onDismissRequest = { install = false }, title = { Text(stringResource(R.string.docker_control_install)) }, text = { Text(stringResource(R.string.docker_control_install_note)) },
        confirmButton = { Button(enabled = !state.busy, onClick = { install = false; model.install() }) { Text(stringResource(R.string.docker_control_confirm)) } }, dismissButton = { TextButton(onClick = { install = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (cancel) AlertDialog(onDismissRequest = { cancel = false }, title = { Text(stringResource(R.string.operations_request_cancel)) }, text = { Text(stringResource(R.string.operations_cancel_explanation)) },
        confirmButton = { Button(onClick = { cancel = false; model.cancelInstall() }) { Text(stringResource(R.string.common_cancel)) } }, dismissButton = { TextButton(onClick = { cancel = false }) { Text(stringResource(R.string.common_close)) } })
    if (recover) AlertDialog(onDismissRequest = { recover = false }, title = { Text(stringResource(R.string.installation_recover)) }, text = { Column {
        Text(stringResource(R.string.installation_recover_help)); OutlinedTextField(id, { id = it }, singleLine = true, label = { Text(stringResource(R.string.installation_operation_id)) })
        if (state.pendingInstallation) Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(identified, { identified = it }); Text(stringResource(R.string.docker_control_identify)) }
    } }, confirmButton = { Button(enabled = !state.busy && (!state.pendingInstallation || identified) && runCatching { InstallationRoutes.operation(id.trim()) }.isSuccess,
        onClick = { recover = false; model.recoverInstall(id, identified) }) { Text(stringResource(R.string.common_refresh)) } }, dismissButton = { TextButton(onClick = { recover = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable internal fun controlActionLabel(kind: DockerControlKind): String = stringResource(when (kind) {
    DockerControlKind.EngineStart -> R.string.docker_control_engine_start
    DockerControlKind.EngineStop -> R.string.docker_control_engine_stop
    DockerControlKind.EngineRestart -> R.string.docker_control_engine_restart
    DockerControlKind.MirrorCreate -> R.string.docker_control_add_mirror
    DockerControlKind.MirrorUpdate -> R.string.docker_control_edit_mirror
    DockerControlKind.MirrorDelete -> R.string.docker_control_delete_mirror
    DockerControlKind.MirrorSelect -> R.string.docker_control_select
})
@Composable internal fun controlProblem(code: String): String = stringResource(when (code) {
    "docker.not_installed" -> R.string.docker_control_not_installed
    "docker.resources.pending" -> R.string.docker_resources_pending
    "docker.resources.installation_active" -> R.string.docker_resources_installation_active
    "docker.control.facts_changed", "docker.resources.facts_changed" -> R.string.docker_control_conflict
    "docker.engine.problem.platform_unsupported", "docker.manual_host_action_required", "docker.install_not_supported" -> R.string.docker_control_windows
    "docker.engine.problem.helper_unavailable", "docker.access_not_configured", "docker.access_restart_required" -> R.string.docker_control_authorization
    "docker.daemon_unavailable", "docker.connection_failed" -> R.string.docker_control_unavailable
    "docker.engine.problem.action_failed", "docker.engine.problem.desktop_command_failed" -> R.string.docker_control_action_failed
    else -> R.string.docker_control_unverified
})
