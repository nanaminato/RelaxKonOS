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
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

private data class MirrorDraft(val id: String? = null, val name: String = "", val endpoint: String = "") {
    fun request() = DockerMirrorRequest(name.trim(), endpoint.trim())
}
private class MirrorLeaveReview(val editor: Any, val draft: MirrorDraft, val action: () -> Unit)

@Composable internal fun dockerControlViewModel(): DockerControlViewModel {
    val container = appContainer()
    return viewModel(factory = viewModelFactory {
        initializer { DockerControlViewModel(container.session, container.dockerControl, container.dockerResources,
            container.installations, container.operationIndex, container.dockerMutationGate, container.elevationAnswers) }
    })
}

@Composable fun DockerControlScreen(onBack: () -> Unit, modifier: Modifier = Modifier, mirrorsOnly: Boolean = false, recordsOnly: Boolean = false, active: Boolean = true) {
    DockerControlContent(dockerControlViewModel(), appContainer().activeSession, onBack, modifier, mirrorsOnly, recordsOnly, active)
}
@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun DockerControlContent(model: DockerControlViewModel, owner: SessionState.Active?, onBack: () -> Unit,
    modifier: Modifier = Modifier, mirrorsOnly: Boolean = false, recordsOnly: Boolean = false, active: Boolean = true) {
    val state = model.state
    val visible = state.owner === owner; val facts = state.facts.takeIf { visible }
    val manage = visible && owner?.privilegedOperations == true
    val ready = manage && !state.busy && state.pending.isEmpty() && !state.resourcePending && !state.pendingInstallation &&
        state.installation?.state?.active != true && (state.installation == null || state.installationVerified) && facts != null
    var draft by remember(owner) { mutableStateOf<MirrorDraft?>(null) }
    var draftEditor by remember(owner) { mutableStateOf(Any()) }
    val confirmation = model.confirmation?.takeIf { it.owner === owner }
    var leave by remember(owner) { mutableStateOf<MirrorLeaveReview?>(null) }
    val cancel = model.installationCancellation?.takeIf { it.owner === owner }
    val recovery = model.recoveryForm?.takeIf { it.owner === owner }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var lifecycleEpoch by remember(owner, lifecycle) { mutableIntStateOf(0) }
    val observationEpoch = lifecycleEpoch
    val currentActive = rememberUpdatedState(active)
    val currentOwner = rememberUpdatedState(owner)
    val currentInteraction = { currentActive.value && currentOwner.value === owner && model.state.owner === owner &&
        lifecycleEpoch == observationEpoch && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
    DisposableEffect(owner, lifecycle, active) {
        fun withdraw() { model.withdrawReviews(owner); leave = null }
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) { withdraw(); lifecycleEpoch++ }
            if (event == Lifecycle.Event.ON_RESUME) lifecycleEpoch++
        }
        lifecycle.addObserver(observer)
        if (!active) { withdraw(); lifecycleEpoch++ }
        onDispose { lifecycle.removeObserver(observer) }
    }
    val observedDraft = draft
    val observedEditor = draftEditor
    val navigate: (() -> Unit) -> Unit = { action ->
        if (currentInteraction() && !model.state.busy && leave == null && model.confirmation == null &&
            draft === observedDraft && draftEditor === observedEditor) {
            if (observedDraft != null) leave = MirrorLeaveReview(observedEditor, observedDraft, action) else action()
        }
    }
    fun openEditor(value: MirrorDraft) {
        if (currentInteraction() && ready && !model.state.busy && model.state.facts == facts && draftEditor === observedEditor &&
            draft == null && leave == null && model.confirmation == null) { draftEditor = Any(); draft = value }
    }
    LaunchedEffect(owner, state.owner, active) {
        if (active && visible && owner != null && state.facts == null && state.problem == null && !state.busy) model.refresh()
    }
    LaunchedEffect(owner, state.saved) {
        val submitted = state.savedChange
        val editing = draft
        if (visible && state.saved > 0 && editing != null && submitted != null &&
            submitted.kind in setOf(DockerControlKind.MirrorCreate, DockerControlKind.MirrorUpdate) &&
            submitted.target == editing.id && submitted.mirror == editing.request()) draft = null
    }
    LaunchedEffect(owner, active, observationEpoch, state.installation?.operationId, state.installation?.state, state.installationVerified, state.busy, cancel, recovery) {
        if (active && visible) model.observeInstallation {
            active && lifecycleEpoch == observationEpoch && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
        }
    }
    DisposableEffect(owner) { onDispose { model.stop() } }
    BackHandler(active && draft != null) { navigate { draft = null } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(if (recordsOnly) R.string.workspace_records else if (mirrorsOnly) R.string.workspace_mirrors else R.string.docker_title), onBack = { navigate(onBack) })
        if (owner?.capabilities?.contains(ServerCapabilities.DOCKER) != true) { Text(stringResource(R.string.error_capability_missing)); return@Column }
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = { navigate { draft = null; model.refresh() } }) { ActionLabel(R.string.common_refresh) }
        })
        RefreshProgressIndicator(visible = visible && state.busy)
        if (active) OperationMessageDialog(state.problem?.takeIf { visible && !state.busy }?.let { controlProblem(it) },
            eventKey = owner to state.feedbackVersion)
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
            ExecutionStatusChip(stringResource(if (notInstalled) R.string.docker_control_not_installed else if (facts.status.available) R.string.docker_control_running else R.string.docker_control_unavailable), if (facts.status.available) "running" else null, task = false)
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
                        onClick = { model.requestChange(requireNotNull(owner), facts, DockerControlChange(kind)) }) { Text(controlActionLabel(kind)) }
                }
            }
        }
        }
}
        WorkspaceSection(recordsOnly) {
        val recordModel: DockerResourceViewModel = dockerResourceViewModel(key = "docker-resources-all")
        val recordState = recordModel.state
        LaunchedEffect(owner, recordState.owner, recordsOnly, active) { if (active && recordsOnly && recordState.owner === owner) recordModel.refresh() }
        if (recordState.owner === owner) recordState.pending.forEach { marker -> ManagementCard {
            Text(resourceActionLabel(marker.action)); marker.target?.let { Text(it) }
            PageActionRow(refresh = {
                TextButton(enabled = !recordState.busy, onClick = recordModel::refresh) { ActionLabel(R.string.common_refresh) }
            })
            OutlinedButton(enabled = !recordState.busy && recordState.facts?.status?.available == true, onClick = { recordModel.accept(marker) }) { Text(stringResource(R.string.docker_control_accept)) }
        } }
        DockerResourceKind.entries.forEach { kind ->
            val resourceModel: DockerResourceViewModel = dockerResourceViewModel(key = "docker-resources-${kind.name}")
            val resourceState = resourceModel.state
            if (resourceState.owner === owner) {
                resourceState.result?.let { result -> ManagementCard {
                    var showLog by remember(owner, kind, result) { mutableStateOf(false) }
                    Text(resourceKindLabel(kind), style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(if (result.success) R.string.docker_operation_complete else R.string.docker_operation_failed))
                    if (resourceState.pending.isNotEmpty()) Text(stringResource(R.string.docker_resources_pending), color = MaterialTheme.colorScheme.error)
                    result.problemCode.takeIf(String::isNotBlank)?.let { Text(controlProblem(it)) }
                    if (result.logLines.isNotEmpty()) TextButton(onClick = { showLog = !showLog }) { Text(stringResource(if (showLog) R.string.docker_resources_hide_sensitive else R.string.docker_resources_show_sensitive)) }
                    if (showLog) result.logLines.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
                    if (result.logTruncated) Text(stringResource(R.string.docker_resources_truncated))
                } }
            }
        }
        if (visible && state.pendingInstallation) Text(stringResource(R.string.installation_pending, installationServiceLabel(InstallationService.Docker), installationKindLabel(InstallationKind.Install)), color = MaterialTheme.colorScheme.error)
        key(observationEpoch, state.feedbackVersion) {
            if (manage) TextButton(enabled = !state.busy, onClick = {
                if (currentInteraction() && model.state.feedbackVersion == state.feedbackVersion) model.requestRecovery(requireNotNull(owner))
            }) { Text(stringResource(R.string.installation_recover)) }
        }
        if (visible && state.installationLookupFailed) ManagementCard {
            state.installationLookupId?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            Text(stringResource(if (state.installationLookupMissing) R.string.operations_missing else R.string.operations_unverified))
            Text(stringResource(R.string.docker_installation_record_note))
        }
        if (visible) state.installation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                ExecutionStatusChip(installationStateLabel(operation.state), operation.state.wire); Text(installationStageLabel(operation.stage))
                operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.let { Text(installationProblemLabel(it)) }
                if (operation.state.active && operation.cancellable && manage) OutlinedButton(enabled = !state.busy, onClick = { owner?.let { model.requestInstallationCancellation(it, operation) } }) { Text(stringResource(R.string.operations_request_cancel)) }
            } else Text(stringResource(R.string.operations_unverified))
            }
        }
        }
WorkspaceSection(mirrorsOnly) {
        Text(stringResource(R.string.docker_control_mirrors), style = MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.docker_control_mirror_note))
        if (facts != null) {
            key(observationEpoch, draftEditor) {
                if (manage) OutlinedButton(enabled = ready && draft == null, onClick = { openEditor(MirrorDraft()) }) { Text(stringResource(R.string.docker_control_add_mirror)) }
            }
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
                                TextButton(enabled = ready && !mirror.selected && draft == null, onClick = { model.requestChange(requireNotNull(owner), facts, DockerControlChange(DockerControlKind.MirrorSelect, mirror.id)) }) { Text(stringResource(R.string.docker_control_select)) }
                                if (!mirror.default) {
                                    key(observationEpoch, draftEditor) {
                                        TextButton(enabled = ready && draft == null, onClick = { openEditor(MirrorDraft(mirror.id, mirror.name, mirror.endpoint)) }) { Text(stringResource(R.string.docker_control_edit_mirror)) }
                                    }
                                    TextButton(enabled = ready && draft == null, onClick = { model.requestChange(requireNotNull(owner), facts, DockerControlChange(DockerControlKind.MirrorDelete, mirror.id)) }) { ActionLabel(R.string.common_delete) }
                                }
                            }
                            }
                        }
                    }
                    draft?.let { editing ->
                    val editor = draftEditor
                    val currentEditor = { currentInteraction() && draftEditor === editor && draft === editing &&
                        !model.state.busy && leave == null && model.confirmation == null }
                    key(editor, observationEpoch) { Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text(stringResource(if (editing.id == null) R.string.docker_control_add_mirror else R.string.docker_control_edit_mirror))
                        OutlinedTextField(editing.name, { if (currentEditor()) draft = editing.copy(name = it) }, enabled = ready, singleLine = true, label = { Text(stringResource(R.string.docker_control_mirror_name)) })
                        OutlinedTextField(editing.endpoint, { if (currentEditor()) draft = editing.copy(endpoint = it) }, enabled = ready, singleLine = true, label = { Text(stringResource(R.string.docker_control_mirror_endpoint)) })
                        Text(stringResource(R.string.docker_control_mirror_format))
                        key(editing) {
                        Button(enabled = ready && DockerMirrorValidation.valid(editing.request()), onClick = {
                            if (currentEditor() && model.state.facts == facts)
                                model.requestChange(requireNotNull(owner), facts, DockerControlChange(if (editing.id == null) DockerControlKind.MirrorCreate else DockerControlKind.MirrorUpdate, editing.id, editing.request()))
                        }) { ActionLabel(R.string.common_save) }
                        TextButton(enabled = !state.busy, onClick = { if (currentEditor()) navigate { draft = null } }) { Text(stringResource(R.string.common_cancel)) }
                        }
                    } } }
                }
            }
        }
}
            }
    if (active) confirmation?.let { pending -> AlertDialog(onDismissRequest = { model.dismissConfirmation(pending) }, title = { Text(controlActionLabel(pending.change.kind)) }, text = { Column {
        if (pending.change.engine != null) Text(stringResource(R.string.docker_control_engine_warning))
        else {
            Text(stringResource(R.string.docker_control_mirror_warning))
            val target = pending.facts.mirrors.firstOrNull { it.id == pending.change.target }
            target?.let { Text(if (it.default) stringResource(R.string.docker_control_default_mirror) else it.name); if (!it.default) Text(it.endpoint) }
            pending.change.mirror?.let { Text(it.name); Text(it.endpoint) }
            if (pending.change.kind == DockerControlKind.MirrorDelete && target?.selected == true) Text(stringResource(R.string.docker_control_delete_selected))
        }
    } }, confirmButton = { Button(enabled = ready, onClick = { model.change(pending) }) { Text(stringResource(R.string.docker_control_confirm)) } },
        dismissButton = { TextButton(onClick = { model.dismissConfirmation(pending) }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (active) leave?.let { review -> key(review) {
        val currentDecision = { currentInteraction() && !model.state.busy && leave === review &&
            draftEditor === review.editor && draft === review.draft }
        val continueEditing = { if (currentDecision()) leave = null }
        AlertDialog(onDismissRequest = continueEditing, title = { Text(stringResource(R.string.docker_control_discard)) },
            text = { Text(stringResource(R.string.docker_control_discard_note)) }, confirmButton = { TextButton(onClick = {
                if (currentDecision()) { leave = null; draft = null; review.action() }
            }) { Text(stringResource(R.string.docker_control_confirm)) } },
            dismissButton = { TextButton(onClick = continueEditing) { Text(stringResource(R.string.common_cancel)) } })
    } }
    if (active) cancel?.let { request -> AlertDialog(onDismissRequest = { model.dismissInstallationCancellation(request) }, title = { Text(stringResource(R.string.operations_request_cancel)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) { Text(request.operation.operationId); Text(stringResource(R.string.operations_cancel_explanation)) } },
        confirmButton = { Button(enabled = !state.busy && owner === request.owner && state.installationVerified && state.installation == request.operation && request.operation.state.active && request.operation.cancellable,
            onClick = { model.cancelInstall(request) }) { Text(stringResource(R.string.common_cancel)) } },
        dismissButton = { TextButton(onClick = { model.dismissInstallationCancellation(request) }) { Text(stringResource(R.string.common_close)) } }) }
    if (active) recovery?.let { form -> key(form, observationEpoch) {
        val input = form.draft
        val dismiss = { if (currentInteraction()) model.dismissRecovery(form) }
        AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.installation_recover)) }, text = { Column {
            Text(stringResource(R.string.installation_recover_help))
            OutlinedTextField(input.id, { if (currentInteraction()) model.changeRecoveryId(form, it) }, singleLine = true,
                label = { Text(stringResource(R.string.installation_operation_id)) })
            if (form.pending != null) key(input) {
                CheckboxOption(input.identified, stringResource(R.string.docker_control_identify)) {
                    if (currentInteraction()) model.identifyRecovery(form, input, it)
                }
            }
        } }, confirmButton = { key(input) {
            Button(enabled = !state.busy && (form.pending == null || input.identified) && runCatching { InstallationRoutes.operation(input.id.trim()) }.isSuccess,
                onClick = { if (currentInteraction()) model.submitRecovery(form, input) }) { ActionLabel(R.string.common_refresh) }
        } }, dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
    } }
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
@Composable internal fun controlProblem(code: String): String = if (code.startsWith("installation.")) installationProblemLabel(code) else stringResource(when (code) {
    "docker.control.installation_refresh_failed" -> R.string.docker_control_installation_refresh_failed
    "docker.control.refresh_failed" -> R.string.docker_control_refresh_failed
    "docker.control.recovery_storage_failed" -> R.string.docker_control_recovery_storage_failed
    "docker.not_installed" -> R.string.docker_control_not_installed
    "docker.resources.pending" -> R.string.docker_resources_pending
    "docker.resources.installation_active" -> R.string.docker_resources_installation_active
    "docker.control.facts_changed", "docker.resources.facts_changed" -> R.string.docker_control_conflict
    "docker.engine.problem.platform_unsupported", "docker.manual_host_action_required", "docker.install_not_supported" -> R.string.docker_control_windows
    "docker.engine.problem.helper_unavailable", "docker.access_not_configured" -> R.string.docker_control_authorization
    "docker.access_restart_required" -> R.string.docker_access_restart_required
    "docker.daemon_unavailable", "docker.connection_failed" -> R.string.docker_control_unavailable
    "docker.engine.problem.action_failed", "docker.engine.problem.desktop_command_failed" -> R.string.docker_control_action_failed
    else -> R.string.docker_control_unverified
})
