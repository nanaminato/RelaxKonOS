package app.relaxkonos.mobile.ui.manage.docker

import android.app.Application
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerContainer
import app.relaxkonos.mobile.core.net.DockerImage
import app.relaxkonos.mobile.core.net.DockerLogs
import app.relaxkonos.mobile.core.net.DockerNetwork
import app.relaxkonos.mobile.core.net.DockerStack
import app.relaxkonos.mobile.core.net.DockerStackOperation
import app.relaxkonos.mobile.core.net.DockerStackOperationDiagnostics
import app.relaxkonos.mobile.core.net.DockerStackOperationKind
import app.relaxkonos.mobile.core.net.DockerStackOperationState
import app.relaxkonos.mobile.core.net.DockerStackPreview
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.DockerVolume
import app.relaxkonos.mobile.core.net.DockerVolumeDetails
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

data class DockerScreenState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val busy: Boolean = false,
    val status: ApiResult<DockerStatus>? = null,
    val containers: ApiResult<List<DockerContainer>>? = null,
    val images: ApiResult<List<DockerImage>>? = null,
    val networks: ApiResult<List<DockerNetwork>>? = null,
    val volumes: ApiResult<List<DockerVolume>>? = null,
    val stacks: ApiResult<List<DockerStack>>? = null,
    val selectedStack: DockerStack? = null,
    val services: ApiResult<List<DockerStackService>>? = null,
    val operations: ApiResult<List<DockerStackOperation>>? = null,
    /** Bounded output of the step behind the most recent operation outcome. */
    val diagnostics: DockerStackOperationDiagnostics? = null,
    val logs: Pair<String, DockerLogs>? = null,
    val volume: Pair<String, DockerVolumeDetails>? = null,
    /** What the server's Compose parser resolved for the current draft. Null until a preview runs. */
    val preview: DockerStackPreview? = null,
    val message: UiMessage? = null,
)

class DockerViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(DockerScreenState(owner = container.activeSession))
        private set

    init { refresh() }

    fun refresh() {
        val owner = container.activeSession ?: return
        if (ServerCapabilities.DOCKER !in owner.capabilities || state.loading) return
        state = state.copy(owner = owner, loading = true, message = null)
        viewModelScope.launch {
            // Every result is independent: an unavailable image listing must not erase the stack
            // list that lets a user diagnose it.
            val status = container.docker.status(owner)
            val containers = container.docker.containers(owner)
            val images = container.docker.images(owner)
            val networks = container.docker.networks(owner)
            val volumes = container.docker.volumes(owner)
            val stacks = container.docker.stacks(owner)
            if (container.activeSession === owner) state = state.copy(
                loading = false, status = status, containers = containers, images = images,
                networks = networks, volumes = volumes, stacks = stacks,
            )
        }
    }

    fun selectStack(stack: DockerStack) {
        val owner = state.owner ?: return
        state = state.copy(selectedStack = stack, services = null, operations = null, diagnostics = null, logs = null)
        viewModelScope.launch {
            val services = container.docker.services(owner, stack.name)
            val operations = container.docker.stackOperations(owner, stack.name)
            if (container.activeSession === owner && state.selectedStack?.name == stack.name) {
                state = state.copy(services = services, operations = operations)
                // An operation started elsewhere (another phone, or before this device reconnected) is
                // still running; keep reading the durable record rather than showing a stale snapshot.
                val active = (operations as? ApiResult.Success)?.value?.firstOrNull { it.state.active }
                if (active != null) track(owner, active)
            }
        }
    }

    fun preview(name: String, yaml: String) {
        val owner = state.owner ?: return
        if (state.busy || name.isBlank() || yaml.isBlank()) { state = state.copy(message = UiMessage(R.string.docker_stack_required)); return }
        state = state.copy(busy = true, message = null, preview = null)
        viewModelScope.launch {
            val result = container.docker.previewStack(owner, name.trim(), yaml)
            if (container.activeSession !== owner) return@launch
            state = state.copy(busy = false, preview = (result as? ApiResult.Success)?.value,
                message = when (result) {
                    is ApiResult.Success -> UiMessage(R.string.docker_stack_preview_ready, listOf(result.value.services.size, result.value.volumes.size, result.value.networks.size), tone = StatusTone.Success)
                    // A refusal is reported as the server's verdict. The screen never rewrites a
                    // rejected definition into something else, and it never claims a cause it did
                    // not verify.
                    else -> result.dockerFailure()
                })
        }
    }

    fun clearPreview() { state = state.copy(preview = null) }

    /**
     * Deploys the approved definition. The preview is taken here, immediately before the submission,
     * so the version the server verifies is the version of the document that is being sent — not an
     * answer from an earlier click that the operator may have edited away.
     */
    fun deploy(name: String, yaml: String) {
        val owner = state.owner ?: return
        if (state.busy || name.isBlank() || yaml.isBlank()) { state = state.copy(message = UiMessage(R.string.docker_stack_required)); return }
        state = state.copy(busy = true, message = null)
        viewModelScope.launch {
            val preview = container.docker.previewStack(owner, name.trim(), yaml)
            if (preview !is ApiResult.Success) {
                // Nothing was submitted: the deployment never left the device.
                if (container.activeSession === owner) state = state.copy(busy = false, message = preview.dockerFailure())
                return@launch
            }
            val submitted = container.docker.deployStack(owner, name.trim(), yaml, preview.value.definitionVersion)
            if (container.activeSession !== owner) return@launch
            state = state.copy(busy = false, preview = preview.value)
            when (submitted) {
                is ApiResult.Success -> {
                    state = state.copy(message = UiMessage(R.string.docker_stack_submitted, tone = StatusTone.Success))
                    track(owner, submitted.value)
                }
                else -> state = state.copy(message = submitted.dockerFailure())
            }
        }
    }

    fun stackAction(stack: DockerStack, action: String, confirmed: Boolean) {
        val owner = state.owner ?: return
        if (state.busy) return
        state = state.copy(busy = true, message = null)
        viewModelScope.launch {
            val result = container.docker.stackAction(owner, stack.name, action, confirmed)
            if (container.activeSession !== owner) return@launch
            state = state.copy(busy = false)
            when (result) {
                is ApiResult.Success -> { state = state.copy(message = UiMessage(R.string.docker_stack_submitted, tone = StatusTone.Success)); track(owner, result.value) }
                else -> state = state.copy(message = result.dockerFailure())
            }
        }
    }

    fun cancelOperation(operationId: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = container.docker.cancelStackOperation(owner, operationId)
            if (container.activeSession !== owner) return@launch
            state = state.copy(message = when (result) {
                is ApiResult.Success -> UiMessage(R.string.docker_stack_cancel_requested, tone = StatusTone.Neutral)
                else -> result.dockerFailure()
            })
        }
    }

    fun loadLogs(id: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = container.docker.logs(owner, id, 200)
            if (container.activeSession !== owner) return@launch
            if (state.logs?.first == id) state = state.copy(logs = null)
            else if (result is ApiResult.Success) state = state.copy(logs = id to result.value)
            else state = state.copy(message = result.dockerFailure())
        }
    }

    /** Reads the reference list behind a volume. Deleting an in-use volume is refused by the server. */
    fun loadVolume(name: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = container.docker.volumeDetails(owner, name)
            if (container.activeSession !== owner) return@launch
            if (state.volume?.first == name) state = state.copy(volume = null)
            else if (result is ApiResult.Success) state = state.copy(volume = name to result.value)
            else state = state.copy(message = result.dockerFailure())
        }
    }

    fun importCompose(uri: Uri, onImported: (String) -> Unit) {
        viewModelScope.launch {
            val source = runCatching {
                getApplication<RelaxKonApplication>().contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }
                    ?: error("unreadable document")
            }.getOrNull()
            if (source == null) state = state.copy(message = UiMessage(R.string.docker_import_failed)) else onImported(source)
        }
    }

    fun dismissMessage() { state = state.copy(message = null) }

    /**
     * Follows a durable operation until the server reports a terminal state. The record, not this
     * loop, is the authority: if the window closes the operation keeps running on the server and its
     * result is still readable from the project's operation list.
     */
    private fun track(owner: SessionState.Active, operation: DockerStackOperation) {
        viewModelScope.launch {
            var current = operation
            var elapsed = 0
            while (current.state.active && elapsed < POLL_LIMIT_SECONDS) {
                delay(POLL_INTERVAL_MILLIS)
                elapsed += POLL_INTERVAL_SECONDS
                val next = container.docker.stackOperation(owner, current.operationId)
                if (next is ApiResult.Success) current = next.value else break
            }
            if (container.activeSession !== owner) return@launch
            val diagnostics = (container.docker.stackOperationDiagnostics(owner, current.operationId) as? ApiResult.Success)?.value
            state = state.copy(diagnostics = diagnostics, message = operationMessage(current))
            if (state.selectedStack?.name == current.projectName) {
                val services = container.docker.services(owner, current.projectName)
                val operations = container.docker.stackOperations(owner, current.projectName)
                if (container.activeSession === owner && state.selectedStack?.name == current.projectName)
                    state = state.copy(services = services, operations = operations)
            }
            refresh()
        }
    }

    /**
     * The outcome sentence. A partial failure and an interrupted operation are reported as results
     * that need a decision, never as a plain failure and never as a success.
     */
    private fun operationMessage(operation: DockerStackOperation): UiMessage = when (operation.state) {
        DockerStackOperationState.SUCCEEDED -> UiMessage(R.string.docker_stack_operation_succeeded, listOf(operation.projectName), tone = StatusTone.Success)
        DockerStackOperationState.PARTIAL_FAILED -> UiMessage(R.string.docker_stack_operation_partial, listOf(operation.projectName), tone = StatusTone.Warning)
        DockerStackOperationState.INTERRUPTED -> UiMessage(R.string.docker_stack_operation_interrupted, listOf(operation.projectName), tone = StatusTone.Warning)
        DockerStackOperationState.CANCELLED -> UiMessage(R.string.docker_stack_operation_cancelled, listOf(operation.projectName), tone = StatusTone.Neutral)
        DockerStackOperationState.FAILED -> UiMessage(R.string.docker_stack_operation_failed, listOf(operation.projectName))
        else -> UiMessage(R.string.docker_stack_operation_pending, listOf(operation.projectName), tone = StatusTone.Info)
    }

    private companion object {
        const val POLL_INTERVAL_MILLIS = 1_000L
        const val POLL_INTERVAL_SECONDS = 1
        /** Five minutes of polling. Past that the phone stops watching, but the record still exists. */
        const val POLL_LIMIT_SECONDS = 300
    }
}

// A destructive action awaiting confirmation. The question itself is resolved inside the dialog:
// the click handlers that raise it are not composable contexts.
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
fun DockerScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier, initialStackName: String? = null, onOpenResources: () -> Unit, onOpenControl: () -> Unit, onOpenProxy: () -> Unit) {
    val viewModel: DockerViewModel = androidx.lifecycle.viewmodel.compose.viewModel()
    val state = viewModel.state
    val available = state.owner?.capabilities?.contains(ServerCapabilities.DOCKER) == true
    val notInstalled = (state.status as? ApiResult.Success)?.value?.problemCode == "docker.not_installed"
    var composer by remember { mutableStateOf(false) }
    var composeDraft by remember { mutableStateOf(DEFAULT_COMPOSE) }
    var destructive by remember { mutableStateOf<Pair<DockerRemoval, () -> Unit>?>(null) }
    LaunchedEffect(initialStackName, state.stacks) {
        val stack = (state.stacks as? ApiResult.Success)?.value?.firstOrNull { it.name == initialStackName }
        if (stack != null && state.selectedStack?.name != stack.name) viewModel.selectStack(stack)
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> if (uri != null) viewModel.importCompose(uri) { yaml -> composeDraft = yaml; composer = true } }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.docker_title), onBack = onBack,
            trailing = { Row { if (!notInstalled) { TextButton(onClick = { picker.launch(arrayOf("text/yaml", "application/x-yaml", "text/plain")) }, enabled = available) { Text(stringResource(R.string.docker_import)) }
                TextButton(onClick = { composer = true }, enabled = available) { Text(stringResource(R.string.docker_new_stack)) } }
                TextButton(onClick = viewModel::refresh, enabled = available && !state.loading) { Text(stringResource(R.string.common_refresh)) } } },
        )
        if (!available) { EmptyHint(stringResource(R.string.error_capability_missing)); return@Column }
        state.message?.let { message -> ErrorBanner(message.text(), viewModel::refresh, viewModel::dismissMessage, tone = message.tone) }
        if (state.loading || state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (!notInstalled) TextButton(onClick = onOpenResources) { Text(stringResource(R.string.docker_resources_title)) }
        TextButton(onClick = onOpenControl) { Text(stringResource(if (notInstalled) R.string.docker_control_install else R.string.docker_control_title)) }
        TextButton(onClick = onOpenProxy) { Text(stringResource(R.string.proxy_title)) }
        if (notInstalled) {
            EmptyHint(stringResource(R.string.runtime_install_hint, "Docker"))
        } else {
        DockerStatusCard(state.status)
        LazyColumn(verticalArrangement = Arrangement.spacedBy(Spacing.md), modifier = Modifier.weight(1f)) {
            item {
                DockerStacks(state, viewModel,
                    onDelete = { destructive = DockerRemoval.Stack(it.name) to { viewModel.stackAction(it, "delete", confirmed = true) } },
                    onCancel = viewModel::cancelOperation)
            }
            item { SimpleList(stringResource(R.string.docker_containers), state.containers) { "${it.names} · ${it.status}" } }
            item { SimpleList(stringResource(R.string.docker_images), state.images) { "${it.repository}:${it.tag} · ${it.size}" } }
            item { SimpleList(stringResource(R.string.docker_volumes), state.volumes) { "${it.name} · ${it.driver}" } }
            item { SimpleList(stringResource(R.string.docker_networks), state.networks) { "${it.name} · ${it.driver}" } }
        }
        }
    }
    if (composer) DockerComposer(
        initialYaml = composeDraft, preview = state.preview, busy = state.busy,
        onPreview = viewModel::preview, onClearPreview = viewModel::clearPreview,
        onDismiss = { composer = false },
        onDeploy = { name, yaml -> viewModel.deploy(name, yaml); composer = false })
    destructive?.let { (removal, confirm) -> AlertDialog(onDismissRequest = { destructive = null }, title = { Text(stringResource(R.string.docker_confirm_title)) }, text = { Text(removalMessage(removal)) },
        confirmButton = { Button(onClick = { destructive = null; confirm() }) { Text(stringResource(R.string.common_delete)) } }, dismissButton = { TextButton(onClick = { destructive = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}

private const val DEFAULT_COMPOSE = "services:\n  app:\n    image: nginx:alpine\n"

@Composable private fun DockerStatusCard(status: ApiResult<DockerStatus>?) = SectionCard(stringResource(R.string.docker_runtime)) {
    when (status) { is ApiResult.Success -> Text(if (status.value.available) stringResource(R.string.docker_ready, status.value.serverVersion) else stringResource(R.string.docker_unavailable))
        null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_unavailable), color = MaterialTheme.colorScheme.error) }
}

@Composable private fun DockerStacks(state: DockerScreenState, viewModel: DockerViewModel, onDelete: (DockerStack) -> Unit, onCancel: (String) -> Unit) = SectionCard(stringResource(R.string.docker_stacks)) {
    when (val stacks = state.stacks) { is ApiResult.Success -> if (stacks.value.isEmpty()) Text(stringResource(R.string.docker_empty_stacks)) else stacks.value.forEach { stack ->
        ListRow(stack.name, subtitle = stack.status, leading = { DesktopIcon(R.drawable.ic_app_docker, size = 22.dp) }, onClick = { viewModel.selectStack(stack) },
            trailing = { Row { TextButton(onClick = { viewModel.stackAction(stack, "restart", confirmed = false) }) { Text(stringResource(R.string.docker_restart)) }; TextButton(onClick = { onDelete(stack) }) { Text(stringResource(R.string.common_delete)) } } })
        if (state.selectedStack?.name == stack.name) {
            StackServices(state.services, state.logs, viewModel::loadLogs)
            StackOperations(state.operations, state.diagnostics, viewModel, stack, onCancel)
        }
    }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) }
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
        null -> Text(stringResource(R.string.common_loading))
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
        null -> Text(stringResource(R.string.common_loading))
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

@Composable private fun <T> SimpleList(title: String, result: ApiResult<List<T>>?, text: (T) -> String) = SectionCard(title) { when (result) { is ApiResult.Success -> if (result.value.isEmpty()) Text(stringResource(R.string.docker_empty_resources)) else result.value.forEach { Text(text(it)) }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) } }

@Composable private fun DockerComposer(
    initialYaml: String, preview: DockerStackPreview?, busy: Boolean,
    onPreview: (String, String) -> Unit, onClearPreview: () -> Unit,
    onDismiss: () -> Unit, onDeploy: (String, String) -> Unit,
) {
    var name by mutableStateOf(""); var yaml by mutableStateOf(initialYaml)
    Dialog(onDismissRequest = { if (!busy) onDismiss() }, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize()) {
                ScreenHeader(stringResource(R.string.docker_new_stack), onBack = if (!busy) onDismiss else null,
                    modifier = Modifier.padding(horizontal = Spacing.lg, vertical = Spacing.md))
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    Text(stringResource(R.string.docker_compose_note), style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(name, { name = it; onClearPreview() }, label = { Text(stringResource(R.string.docker_stack_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(yaml, { yaml = it; onClearPreview() }, label = { Text(stringResource(R.string.docker_compose_yaml)) }, minLines = 8, modifier = Modifier.fillMaxWidth())
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
                    TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.common_cancel)) }
                    TextButton(onClick = { onPreview(name, yaml) }, enabled = !busy && name.isNotBlank() && yaml.isNotBlank()) { Text(stringResource(R.string.docker_preview)) }
                    Button(onClick = { onDeploy(name, yaml) }, enabled = !busy && name.isNotBlank() && yaml.isNotBlank()) { Text(stringResource(R.string.docker_deploy)) }
                }
            }
        }
    }
}
