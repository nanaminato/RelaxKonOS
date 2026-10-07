package app.relaxkonos.mobile.ui.manage.docker

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import app.relaxkonos.mobile.ui.common.*
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.key
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerLogs
import app.relaxkonos.mobile.core.net.DockerStack
import app.relaxkonos.mobile.core.net.DockerStackOperation
import app.relaxkonos.mobile.core.net.DockerStackOperationDiagnostics
import app.relaxkonos.mobile.core.net.DockerStackOperationKind
import app.relaxkonos.mobile.core.net.DockerStackOperationState
import app.relaxkonos.mobile.core.net.DockerStackPreview
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

private sealed interface DockerRemoval {
    data class Stack(val name: String) : DockerRemoval
}

@Composable
private fun removalMessage(removal: DockerRemoval): String = when (removal) {
    is DockerRemoval.Stack -> stringResource(R.string.docker_confirm_remove_stack, removal.name)
}

/** The state word shown next to an operation, and the tone that goes with it. */
@Composable
private fun operationState(result: DockerStackOperationState): String = stringResource(
    when (result) {
        DockerStackOperationState.QUEUED -> R.string.docker_stack_state_queued
        DockerStackOperationState.RUNNING -> R.string.docker_stack_state_running
        DockerStackOperationState.SUCCEEDED -> R.string.docker_stack_state_succeeded
        DockerStackOperationState.PARTIAL_FAILED -> R.string.docker_stack_state_partial
        DockerStackOperationState.FAILED -> R.string.docker_stack_state_failed
        DockerStackOperationState.CANCELLED -> R.string.docker_stack_state_cancelled
        DockerStackOperationState.INTERRUPTED -> R.string.docker_stack_state_interrupted
        DockerStackOperationState.UNKNOWN -> R.string.docker_stack_state_unknown
    },
)

@Composable
private fun operationKind(kind: DockerStackOperationKind): String = stringResource(
    when (kind) {
        DockerStackOperationKind.DEPLOY -> R.string.docker_deploy
        DockerStackOperationKind.START -> R.string.docker_start
        DockerStackOperationKind.STOP -> R.string.docker_stop
        DockerStackOperationKind.RESTART -> R.string.docker_restart
        DockerStackOperationKind.DELETE -> R.string.common_delete
        DockerStackOperationKind.UNKNOWN -> R.string.docker_stack_state_unknown
    },
)

@Composable
fun DockerScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier, initialStackName: String? = null, section: String = "overview") {
    val viewModel: DockerViewModel = androidx.lifecycle.viewmodel.compose.viewModel()
    val state = viewModel.state
    val available = state.owner?.capabilities?.contains(ServerCapabilities.DOCKER) == true
    val notInstalled = (state.status as? ApiResult.Success)?.value?.problemCode == "docker.not_installed"
    var composer by remember(state.owner) { mutableStateOf(false) }
    var composeDraft by remember(state.owner) { mutableStateOf(DEFAULT_COMPOSE) }
    var destructive by remember(state.owner) { mutableStateOf<Pair<DockerRemoval, () -> Unit>?>(null) }
    LaunchedEffect(initialStackName, state.stacks) {
        val stack = (state.stacks as? ApiResult.Success)?.value?.firstOrNull { it.name == initialStackName }
        if (stack != null && state.selectedStack?.name != stack.name) viewModel.selectStack(stack)
    }
    val picker = rememberLauncherForActivityResult(rememberUsageOpenDocument("DockerScreen.compose-import")) { uri -> if (uri != null) viewModel.importCompose(uri) { yaml -> viewModel.clearPreview(); composeDraft = yaml; composer = true } }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.docker_title), onBack = onBack,
            trailing = { Row { if (!notInstalled && section == "compose") { TextButton(onClick = { picker.launch(arrayOf("text/yaml", "application/x-yaml", "text/plain")) }, enabled = available) { Text(stringResource(R.string.docker_import)) }
                TextButton(onClick = { viewModel.clearPreview(); composer = true }, enabled = available && !state.busy) { Text(stringResource(R.string.docker_new_stack)) } }
                TextButton(onClick = viewModel::refresh, enabled = available && !state.loading) { ActionLabel(R.string.common_refresh) } } },
        )
        if (!available) { EmptyHint(stringResource(R.string.error_capability_missing)); return@Column }
        if (!composer) state.message?.let { message -> ActionFeedback(message, viewModel::refresh, viewModel::dismissMessage) }
        RefreshProgressIndicator(visible = state.loading || state.busy)
        if (notInstalled) {
            EmptyHint(stringResource(R.string.runtime_install_hint, "Docker"))
        } else {
        DockerStatusCard(state.status)
        LazyColumn(verticalArrangement = Arrangement.spacedBy(Spacing.md), modifier = Modifier.weight(1f)) {
            if (section == "compose") item {
                DockerStacks(state, viewModel,
                    onDelete = { destructive = DockerRemoval.Stack(it.name) to { viewModel.stackAction(it, "delete", confirmed = true) } },
                    onCancel = viewModel::cancelOperation)
            }
            if (section == "overview") item { SimpleList(stringResource(R.string.docker_containers), state.containers) { "${it.names} · ${it.status}" } }
            if (section == "overview") item { SimpleList(stringResource(R.string.docker_images), state.images) { "${it.repository}:${it.tag} · ${it.size}" } }
            if (section == "overview") item { SimpleList(stringResource(R.string.docker_volumes), state.volumes) { "${it.name} · ${it.driver}" } }
            if (section == "overview") item { SimpleList(stringResource(R.string.docker_networks), state.networks) { "${it.name} · ${it.driver}" } }
        }
        }
    }
    if (composer) key(state.owner) { DockerComposer(
        initialYaml = composeDraft, preview = state.preview, busy = state.busy,
        message = state.message, onDismissMessage = viewModel::dismissMessage,
        onPreview = viewModel::preview, onClearPreview = viewModel::clearPreview,
        onDismiss = { composer = false },
        onDeploy = { name, yaml -> viewModel.deploy(name, yaml) { composer = false } }) }
    destructive?.let { (removal, confirm) -> AlertDialog(onDismissRequest = { destructive = null }, title = { Text(stringResource(R.string.docker_confirm_title)) }, text = { Text(removalMessage(removal)) },
        confirmButton = { Button(onClick = { destructive = null; confirm() }) { ActionLabel(R.string.common_delete) } }, dismissButton = { TextButton(onClick = { destructive = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}

private const val DEFAULT_COMPOSE = "services:\n  app:\n    image: nginx:alpine\n"

@Composable private fun DockerStatusCard(status: ApiResult<DockerStatus>?) = SectionCard(stringResource(R.string.docker_runtime)) {
    when (status) { is ApiResult.Success -> ExecutionStatusChip(if (status.value.available) stringResource(R.string.docker_ready, status.value.serverVersion) else stringResource(R.string.docker_unavailable), if (status.value.available) "running" else null, task = false)
        null -> ActivityIndicator(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_unavailable), color = MaterialTheme.colorScheme.error) }
}

@Composable private fun DockerStacks(state: DockerScreenState, viewModel: DockerViewModel, onDelete: (DockerStack) -> Unit, onCancel: (String) -> Unit) = SectionCard(stringResource(R.string.docker_stacks)) {
    when (val stacks = state.stacks) { is ApiResult.Success -> if (stacks.value.isEmpty()) Text(stringResource(R.string.docker_empty_stacks)) else stacks.value.forEach { stack ->
        ListRow(stack.name, subtitle = stack.status, leading = { DesktopIcon(R.drawable.ic_app_docker, size = 22.dp) }, onClick = { viewModel.selectStack(stack) },
            trailing = { Row { TextButton(onClick = { viewModel.stackAction(stack, "restart", confirmed = false) }) { Text(stringResource(R.string.docker_restart)) }; TextButton(onClick = { onDelete(stack) }) { ActionLabel(R.string.common_delete) } } })
        if (state.selectedStack?.name == stack.name) {
            StackServices(state.services, state.logs, viewModel::loadLogs)
            StackOperations(state.operations, state.diagnostics, viewModel, stack, onCancel)
        }
    }; null -> ActivityIndicator(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) }
}

@Composable private fun StackServices(services: ApiResult<List<DockerStackService>>?, logs: Pair<String, DockerLogs>?, onLogs: (String) -> Unit) {
    when (services) {
        is ApiResult.Success -> services.value.forEach { service ->
            ListRow(service.service, subtitle = "${service.state} · ${service.container}",
                trailing = { TextButton(onClick = { onLogs(service.container) }) { Text(stringResource(R.string.docker_service_logs)) } })
            logs?.takeIf { it.first == service.container }?.let { (_, value) ->
                Text(value.lines.joinToString("\n"), style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.fillMaxWidth().padding(start = Spacing.md))
                if (value.truncated) Text(stringResource(R.string.docker_logs_truncated), style = MaterialTheme.typography.bodySmall)
            }
        }
        null -> ActivityIndicator(stringResource(R.string.common_loading))
        else -> Text(stringResource(R.string.docker_list_failed))
    }
}

/**
 * The durable operation history of one project. A failed or partly failed operation keeps its problem
 * code visible and offers the two follow-ups that actually exist: deploy again (Compose's own update
 * path) or stop the project. Nothing here claims a rollback.
 */
@Composable private fun StackOperations(
    operations: ApiResult<List<DockerStackOperation>>?, diagnostics: DockerStackOperationDiagnostics?,
    viewModel: DockerViewModel, stack: DockerStack, onCancel: (String) -> Unit,
) = Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
    Text(stringResource(R.string.docker_stack_operations), style = MaterialTheme.typography.labelLarge)
    when (operations) {
        is ApiResult.Success -> if (operations.value.isEmpty()) Text(stringResource(R.string.docker_stack_no_operations), style = MaterialTheme.typography.bodySmall)
        else operations.value.forEach { operation ->
            ListRow(
                "${operationKind(operation.kind)} · ${operationState(operation.state)}",
                subtitle = operation.problemCode,
                // While running, the stage says where it is. Once it needs attention, the recovery code
                // says what the server could not do — which is what decides between retrying and
                // stopping the project.
                supporting = if (operation.state.active) operation.stage else operation.recoveryProblemCode,
                trailing = {
                    Row {
                        if (operation.state.active && operation.cancellable) TextButton(onClick = { onCancel(operation.operationId) }) { Text(stringResource(R.string.docker_cancel)) }
                        if (operation.state.needsAttention) {
                            TextButton(onClick = { viewModel.stackAction(stack, "stop", confirmed = false) }) { Text(stringResource(R.string.docker_stop)) }
                        }
                    }
                },
            )
            // The observed services are the outcome of the operation, not a prediction of it.
            operation.services.forEach { observed ->
                Text("· ${observed.service} · ${observed.state}", style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.fillMaxWidth().padding(start = Spacing.md))
            }
        }
        null -> ActivityIndicator(stringResource(R.string.common_loading))
        else -> Text(stringResource(R.string.docker_list_failed))
    }
    if (diagnostics != null && diagnostics.lines.isNotEmpty()) {
        Text(diagnostics.lines.joinToString("\n"), style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.fillMaxWidth().padding(start = Spacing.md))
        // Truncation is stated rather than hidden: a cut-off command output that looks complete is
        // how an operator concludes the wrong cause.
        if (diagnostics.truncated) Text(stringResource(R.string.docker_logs_truncated), style = MaterialTheme.typography.bodySmall)
    }
}

@Composable private fun <T> SimpleList(title: String, result: ApiResult<List<T>>?, text: (T) -> String) = SectionCard(title) { when (result) { is ApiResult.Success -> if (result.value.isEmpty()) Text(stringResource(R.string.docker_empty_resources)) else result.value.forEach { Text(text(it)) }; null -> ActivityIndicator(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) } }

@Composable internal fun DockerComposer(
    initialYaml: String, preview: DockerStackPreview?, busy: Boolean,
    onPreview: (String, String) -> Unit, onClearPreview: () -> Unit,
    onDismiss: () -> Unit, onDeploy: (String, String) -> Unit,
    message: UiMessage? = null, onDismissMessage: () -> Unit = {},
) {
    var name by remember { mutableStateOf("") }; var yaml by remember(initialYaml) { mutableStateOf(initialYaml) }
    var confirmLeave by remember { mutableStateOf(false) }
    val leave = { if (!busy) { if (name.isNotEmpty() || yaml != initialYaml) confirmLeave = true else onDismiss() } }
    Dialog(onDismissRequest = leave, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Surface(Modifier.fillMaxSize().safeDrawingPadding().imePadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize()) {
                ScreenHeader(stringResource(R.string.docker_new_stack), onBack = if (!busy) leave else null,
                    modifier = Modifier.padding(horizontal = Spacing.lg, vertical = Spacing.md))
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    message?.let { ActionFeedback(it, onRetry = null, onDismiss = onDismissMessage) }
                    Text(stringResource(R.string.docker_compose_note), style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(name, { name = it; onClearPreview() }, enabled = !busy, label = { Text(stringResource(R.string.docker_stack_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(yaml, { yaml = it; onClearPreview() }, enabled = !busy, label = { Text(stringResource(R.string.docker_compose_yaml)) }, minLines = 8, modifier = Modifier.fillMaxWidth())
                    // What the server parsed, shown before anything is applied. It is not a prediction: the preview
                    // is the Composer's own answer, and the deployment sends back the version it returned.
                    preview?.let { value ->
                        Text(stringResource(R.string.docker_stack_preview_version, value.definitionVersion.take(12)), style = MaterialTheme.typography.bodySmall)
                        value.services.forEach { service -> Text("· ${service.service} · ${service.image}", style = MaterialTheme.typography.bodySmall) }
                        if (value.volumes.isNotEmpty()) Text(stringResource(R.string.docker_stack_preview_volumes, value.volumes.joinToString(", ")), style = MaterialTheme.typography.bodySmall)
                        if (value.networks.isNotEmpty()) Text(stringResource(R.string.docker_stack_preview_networks, value.networks.joinToString(", ")), style = MaterialTheme.typography.bodySmall)
                    }
                }
                HorizontalDivider()
                FlowRow(Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    TextButton(onClick = leave, enabled = !busy) { Text(stringResource(R.string.common_cancel)) }
                    TextButton(onClick = { onPreview(name, yaml) }, enabled = !busy && name.isNotBlank() && yaml.isNotBlank()) { Text(stringResource(R.string.docker_preview)) }
                    Button(onClick = { onDeploy(name, yaml) }, enabled = !busy && name.isNotBlank() && yaml.isNotBlank()) { Text(stringResource(R.string.docker_deploy)) }
                }
            }
        }
    }
    if (confirmLeave) AlertDialog(
        onDismissRequest = { confirmLeave = false },
        title = { Text(stringResource(R.string.ui_discard_draft_title)) },
        text = { Text(stringResource(R.string.ui_discard_draft_message)) },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.editor_discard_changes)) } },
        dismissButton = { TextButton(onClick = { confirmLeave = false }) { Text(stringResource(R.string.common_cancel)) } },
    )
}
