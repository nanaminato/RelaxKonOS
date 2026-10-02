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
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay
import java.util.UUID

private data class ResourceConfirmation(val facts: DockerResourceFacts, val target: DockerResourceTarget?, val change: DockerResourceChange)
@OptIn(ExperimentalLayoutApi::class)
@Composable fun DockerResourceScreen(onBack: () -> Unit, onOpenControl: () -> Unit, onOpenCompose: (String) -> Unit,
    onOpenApplication: (String) -> Unit, modifier: Modifier = Modifier, resourceKind: DockerResourceKind? = null, active: Boolean = true) {
    val model: DockerResourceViewModel = viewModel(key = "docker-resources-${resourceKind?.name ?: "all"}"); val owner = appContainer().activeSession; val state = model.state
    val visible = owner === state.owner; val facts = state.facts.takeIf { visible }; val target = state.target.takeIf { visible }
    val ready = visible && owner?.privilegedOperations == true && !state.busy && state.blocked == null && state.pending.isEmpty() && facts?.status?.available == true
    var kind by remember(owner) { mutableStateOf(resourceKind ?: DockerResourceKind.Containers) }
    var draft by remember(owner) { mutableStateOf<DockerResourceDraft?>(null) }
    var confirmation by remember(owner) { mutableStateOf<ResourceConfirmation?>(null) }
    var leave by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    var reveal by remember(owner, target?.id) { mutableStateOf(false) }
    val navigate: (() -> Unit) -> Unit = { action -> if (draft != null) leave = action else action() }
    fun confirm(change: DockerResourceChange) { facts?.let { confirmation = ResourceConfirmation(it, target.takeIf { change.target != null }, change) } }
    LaunchedEffect(owner, state.owner) { if (visible && owner != null) model.refresh() }
    LaunchedEffect(owner, state.saved) { if (visible && state.saved > 0) draft = null }
    LaunchedEffect(active, owner, target?.id, target?.container?.state, state.busy, draft, confirmation) {
        if (active && visible && target?.container?.state == "running" && !state.busy && draft == null && confirmation == null) { delay(3000); model.pollStats() }
    }
    DisposableEffect(owner) { onDispose { model.stop() } }
    BackHandler(active && draft != null) { navigate { draft = null } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.docker_resources_title), onBack = { navigate(onBack) })
        if (owner?.capabilities?.contains(ServerCapabilities.DOCKER) != true) { Text(stringResource(R.string.error_capability_missing)); return@Column }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TextButton(enabled = !state.busy, onClick = { navigate { draft = null; model.refresh() } }) { Text(stringResource(R.string.common_refresh)) }
        }
        if (visible && state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        OperationMessageDialog(state.problem?.takeIf { visible && !state.busy }?.let { controlProblem(it) })
        if (visible && state.blocked != null) Text(controlProblem(state.blocked), color = MaterialTheme.colorScheme.error)
        if (visible && (state.pending.isNotEmpty() || state.result != null)) TextButton(onClick = { navigate(onOpenControl) }) { Text(stringResource(R.string.workspace_records)) }
        if (facts == null) Text(stringResource(R.string.docker_control_unverified))
        else if (!facts.status.available) { Text(stringResource(R.string.docker_control_unavailable)); Text(controlProblem(facts.status.problemCode)) }
        else {
            if (resourceKind == null) FlowRow { DockerResourceKind.entries.forEach { item -> FilterChip(selected = kind == item, enabled = !state.busy,
                onClick = { navigate { draft = null; kind = item; model.refresh() } }, label = { Text(resourceKindLabel(item)) }) } }
            val create = when (kind) { DockerResourceKind.Containers -> DockerResourceAction.CreateContainer; DockerResourceKind.Images -> DockerResourceAction.PullImage; DockerResourceKind.Networks -> DockerResourceAction.CreateNetwork; DockerResourceKind.Volumes -> DockerResourceAction.CreateVolume }
            if (owner.privilegedOperations) OutlinedButton(enabled = ready && draft == null, onClick = { draft = DockerResourceDraft(create, driver = if (kind == DockerResourceKind.Networks) "bridge" else "local") }) { Text(resourceActionLabel(create)) }
            val rows = when (kind) {
                DockerResourceKind.Containers -> facts.containers.orEmpty().map { Triple(it.id, it.names, "${it.image} · ${it.state} · ${it.status}") }
                DockerResourceKind.Images -> facts.images.orEmpty().map { Triple(it.id, "${it.repository}:${it.tag}", "${it.id} · ${it.size} · ${it.createdSince}") }
                DockerResourceKind.Networks -> facts.networks.orEmpty().map { Triple(it.id, it.name, "${it.driver} · ${it.scope}") }
                DockerResourceKind.Volumes -> facts.volumes.orEmpty().map { Triple(it.name, it.name, "${it.driver} · ${it.mountpoint}") }
            }
            @Composable fun listing() { SectionCard(resourceKindLabel(kind)) {
                if (rows.isEmpty()) Text(stringResource(R.string.docker_empty_resources))
                rows.forEach { (id, name, subtitle) -> ListRow(name, subtitle = subtitle, onClick = { navigate { draft = null; reveal = false; model.select(kind, id) } }) }
            } }
            @Composable fun detail() {
                target?.takeIf { it.kind == kind }?.let { selected -> SectionCard(stringResource(R.string.docker_resources_detail)) {
                    Text(selected.id)
                    selected.container?.let { c ->
                        Text("${c.name} · ${c.image}"); Text("${c.state} · ${c.status}"); Text(c.created)
                        Text(c.ports.joinToString("\n")); Text(c.mounts.joinToString("\n")); Text(c.networks.joinToString("\n")); Text(c.restartPolicy)
                        if (reveal) { Text(c.command); Text(c.workingDirectory); Text(c.environment.joinToString("\n")) }
                        state.stats?.let { Text(stringResource(R.string.docker_resources_stats, it.cpuPercent, it.memoryUsage, it.networkIo, it.blockIo)) }
                        TextButton(enabled = !state.busy, onClick = model::logs) { Text(stringResource(R.string.docker_service_logs)) }
                        if (reveal) state.logs?.let { Text(it.lines.joinToString("\n")); if (it.truncated) Text(stringResource(R.string.docker_resources_truncated)) }
                    }
                    selected.network?.let { Text("${it.name} · ${it.driver} · ${it.scope}"); Text(stringResource(R.string.docker_resources_references, it.containers.joinToString(", "))) }
                    selected.volume?.let { Text("${it.name} · ${it.driver}"); Text(it.mountpoint); Text(stringResource(R.string.docker_resources_references, it.usedBy.joinToString(", "))); Text(stringResource(R.string.docker_resources_volume_warning)) }
                    TextButton(onClick = { reveal = !reveal }) { Text(stringResource(if (reveal) R.string.docker_resources_hide_sensitive else R.string.docker_resources_show_sensitive)) }
                    if (reveal) selected.labels.forEach { (k, v) -> Text("$k=$v") }
                    if (selected.managed) {
                        Text(stringResource(R.string.docker_resources_managed))
                        selected.stack?.let { stack -> TextButton(onClick = { navigate { onOpenCompose(stack) } }) { Text(stringResource(R.string.docker_stacks)) } }
                        selected.applicationId?.takeIf { ServerCapabilities.APPLICATION_DEPLOYMENTS in owner.capabilities && runCatching { UUID.fromString(it) }.isSuccess }?.let { id ->
                            TextButton(onClick = { navigate { onOpenApplication(id) } }) { Text(stringResource(R.string.docker_resources_application)) }
                        }
                    } else if (owner.privilegedOperations) FlowRow {
                        if (kind == DockerResourceKind.Containers) {
                            TextButton(enabled = ready && draft == null, onClick = { draft = DockerResourceDraft(DockerResourceAction.RenameContainer, name = selected.container!!.name) }) { Text(resourceActionLabel(DockerResourceAction.RenameContainer)) }
                            listOf(DockerResourceAction.Start, DockerResourceAction.Stop, DockerResourceAction.Restart, DockerResourceAction.Pause, DockerResourceAction.Unpause, DockerResourceAction.DeleteContainer).forEach { action ->
                                TextButton(enabled = ready && draft == null && (action != DockerResourceAction.Pause || selected.container?.state == "running") && (action != DockerResourceAction.Unpause || selected.container?.state == "paused"), onClick = { confirm(DockerResourceChange(action, selected.id)) }) { Text(resourceActionLabel(action)) }
                            }
                        } else {
                            val action = when (kind) { DockerResourceKind.Images -> DockerResourceAction.DeleteImage; DockerResourceKind.Networks -> DockerResourceAction.DeleteNetwork; else -> DockerResourceAction.DeleteVolume }
                            val deletable = selected.network?.let { it.containers.isEmpty() && it.name !in setOf("bridge", "host", "none") } ?: selected.volume?.usedBy?.isEmpty() ?: true
                            TextButton(enabled = ready && draft == null && deletable, onClick = { confirm(DockerResourceChange(action, selected.id)) }) { Text(resourceActionLabel(action)) }
                        }
                    }
                } }
                draft?.let { editing -> ResourceEditor(editing, { draft = it }, enabled = !state.busy,
                    onCancel = { navigate { draft = null } }, onReview = { editing.change(target?.id)?.let(::confirm) }, ready = ready) }
            }
            BoxWithConstraints {
                if (maxWidth >= 600.dp) Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) { Column(Modifier.weight(1f)) { listing() }; Column(Modifier.weight(1f)) { detail() } }
                else Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) { listing(); detail() }
            }
        }
    }
    confirmation?.let { frozen -> AlertDialog(onDismissRequest = { confirmation = null }, title = { Text(resourceActionLabel(frozen.change.action)) }, text = { Column {
        Text(frozen.change.target ?: frozen.change.container?.name ?: frozen.change.value.orEmpty())
        Text(stringResource(R.string.docker_resources_confirm))
        if (frozen.change.action == DockerResourceAction.CreateContainer) Text(stringResource(R.string.docker_resources_create_only))
        if (frozen.change.action == DockerResourceAction.DeleteContainer) Text(stringResource(R.string.docker_resources_container_warning))
        if (frozen.change.action == DockerResourceAction.DeleteImage) Text(stringResource(R.string.docker_resources_image_warning))
        if (frozen.change.action == DockerResourceAction.DeleteVolume) Text(stringResource(R.string.docker_resources_volume_warning))
        if (frozen.change.action in listOf(DockerResourceAction.DeleteContainer, DockerResourceAction.Stop)) Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(checked = frozen.change.force, onCheckedChange = { confirmation = frozen.copy(change = frozen.change.copy(force = it)) }); Text(stringResource(if (frozen.change.action == DockerResourceAction.Stop) R.string.docker_resources_force_stop else R.string.docker_resources_force)) }
    } }, confirmButton = { Button(enabled = !state.busy, onClick = { confirmation = null; model.change(frozen.facts, frozen.target, frozen.change) }) { Text(stringResource(R.string.docker_resources_apply)) } }, dismissButton = { TextButton(onClick = { confirmation = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    leave?.let { action -> AlertDialog(onDismissRequest = { leave = null }, title = { Text(stringResource(R.string.docker_resources_discard)) },
        confirmButton = { Button(onClick = { leave = null; draft = null; confirmation = null; action() }) { Text(stringResource(R.string.docker_resources_discard)) } },
        dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}
@Composable private fun ResourceEditor(draft: DockerResourceDraft, update: (DockerResourceDraft) -> Unit, enabled: Boolean, ready: Boolean, onCancel: () -> Unit, onReview: () -> Unit) = SectionCard(resourceActionLabel(draft.action)) {
    @Composable fun field(label: Int, value: String, change: (String) -> DockerResourceDraft, multi: Boolean = false) {
        OutlinedTextField(value, onValueChange = { update(change(it)) }, label = { Text(stringResource(label)) }, enabled = enabled, singleLine = !multi, modifier = Modifier.fillMaxWidth())
    }
    if (draft.action != DockerResourceAction.PullImage) field(R.string.docker_resources_name, draft.name, { draft.copy(name = it) })
    if (draft.action in listOf(DockerResourceAction.CreateContainer, DockerResourceAction.PullImage)) field(R.string.docker_resources_image, draft.image, { draft.copy(image = it) })
    if (draft.action == DockerResourceAction.CreateContainer) {
        Text(stringResource(R.string.docker_resources_lines))
        field(R.string.docker_resources_arguments, draft.arguments, { draft.copy(arguments = it) }, true)
        field(R.string.docker_resources_ports, draft.ports, { draft.copy(ports = it) }, true)
        field(R.string.docker_resources_environment, draft.environment, { draft.copy(environment = it) }, true)
        field(R.string.docker_resources_mounts, draft.mounts, { draft.copy(mounts = it) }, true)
        field(R.string.docker_resources_network, draft.network, { draft.copy(network = it) })
        field(R.string.docker_resources_restart, draft.restart, { draft.copy(restart = it) })
        field(R.string.docker_resources_cpu, draft.cpu, { draft.copy(cpu = it) })
        field(R.string.docker_resources_memory, draft.memory, { draft.copy(memory = it) })
        field(R.string.docker_resources_pids, draft.pids, { draft.copy(pids = it) })
        field(R.string.docker_resources_log_driver, draft.logDriver, { draft.copy(logDriver = it) })
        field(R.string.docker_resources_log_options, draft.logOptions, { draft.copy(logOptions = it) }, true)
    }
    if (draft.action in listOf(DockerResourceAction.CreateNetwork, DockerResourceAction.CreateVolume)) field(R.string.docker_resources_driver, draft.driver, { draft.copy(driver = it) })
    if (draft.action in listOf(DockerResourceAction.CreateContainer, DockerResourceAction.CreateVolume)) field(R.string.docker_resources_labels, draft.labels, { draft.copy(labels = it) }, true)
    Row { TextButton(enabled = enabled, onClick = onCancel) { Text(stringResource(R.string.common_cancel)) }; Button(enabled = ready && draft.change(null) != null, onClick = onReview) { Text(stringResource(R.string.docker_resources_review)) } }
}
@Composable internal fun resourceKindLabel(kind: DockerResourceKind): String = stringResource(when (kind) {
    DockerResourceKind.Containers -> R.string.docker_containers; DockerResourceKind.Images -> R.string.docker_images; DockerResourceKind.Networks -> R.string.docker_networks; DockerResourceKind.Volumes -> R.string.docker_volumes
})
@Composable internal fun resourceActionLabel(action: DockerResourceAction): String = stringResource(when (action) {
    DockerResourceAction.CreateContainer -> R.string.docker_resources_create_container; DockerResourceAction.RenameContainer -> R.string.docker_resources_rename
    DockerResourceAction.Start -> R.string.docker_start; DockerResourceAction.Stop -> R.string.docker_stop; DockerResourceAction.Restart -> R.string.docker_restart
    DockerResourceAction.Pause -> R.string.docker_resources_pause; DockerResourceAction.Unpause -> R.string.docker_resources_unpause
    DockerResourceAction.DeleteContainer -> R.string.docker_resources_delete_container; DockerResourceAction.PullImage -> R.string.docker_resources_pull
    DockerResourceAction.DeleteImage -> R.string.docker_resources_delete_image; DockerResourceAction.CreateNetwork -> R.string.docker_resources_create_network
    DockerResourceAction.DeleteNetwork -> R.string.docker_resources_delete_network; DockerResourceAction.CreateVolume -> R.string.docker_resources_create_volume; DockerResourceAction.DeleteVolume -> R.string.docker_resources_delete_volume
})
