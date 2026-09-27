package app.relaxkonos.mobile.ui.manage.docker

import android.app.Application
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerContainer
import app.relaxkonos.mobile.core.net.DockerImage
import app.relaxkonos.mobile.core.net.DockerNetwork
import app.relaxkonos.mobile.core.net.DockerOperation
import app.relaxkonos.mobile.core.net.DockerStack
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.DockerVolume
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

data class DockerScreenState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val status: ApiResult<DockerStatus>? = null,
    val containers: ApiResult<List<DockerContainer>>? = null,
    val images: ApiResult<List<DockerImage>>? = null,
    val networks: ApiResult<List<DockerNetwork>>? = null,
    val volumes: ApiResult<List<DockerVolume>>? = null,
    val stacks: ApiResult<List<DockerStack>>? = null,
    val selectedStack: DockerStack? = null,
    val services: ApiResult<List<DockerStackService>>? = null,
    val message: UiMessage? = null,
    val actionRunning: Boolean = false,
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
        state = state.copy(selectedStack = stack, services = null)
        viewModelScope.launch {
            val services = container.docker.services(owner, stack.name)
            if (container.activeSession === owner && state.selectedStack?.name == stack.name) state = state.copy(services = services)
        }
    }

    fun deploy(name: String, yaml: String) = runOperation { owner -> container.docker.deployStack(owner, name, yaml) }
    fun containerAction(item: DockerContainer, action: String) = runOperation { owner -> container.docker.containerAction(owner, item.id, action, confirmed = action == "delete") }
    fun stackAction(item: DockerStack, action: String) = runOperation { owner -> container.docker.stackAction(owner, item.name, action, confirmed = action == "delete") }

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

    private fun runOperation(call: suspend (SessionState.Active) -> ApiResult<DockerOperation>) {
        val owner = state.owner ?: return
        if (state.actionRunning) return
        state = state.copy(actionRunning = true, message = null)
        viewModelScope.launch {
            val result = call(owner)
            if (container.activeSession === owner) {
                state = state.copy(actionRunning = false, message = when (result) {
                    is ApiResult.Success -> if (result.value.success) UiMessage(R.string.docker_operation_complete, tone = app.relaxkonos.mobile.ui.common.StatusTone.Success)
                    else UiMessage(R.string.docker_operation_failed)
                    else -> result.failureMessage()
                })
                if (result is ApiResult.Success && result.value.success) refresh()
            }
        }
    }
}

// A destructive action awaiting confirmation. The question itself is resolved inside the dialog:
// the click handlers that raise it are not composable contexts.
private sealed interface DockerRemoval {
    data class Stack(val name: String) : DockerRemoval
    data class Container(val name: String) : DockerRemoval
}

@Composable
private fun removalMessage(removal: DockerRemoval): String = when (removal) {
    is DockerRemoval.Stack -> stringResource(R.string.docker_confirm_remove_stack, removal.name)
    is DockerRemoval.Container -> stringResource(R.string.docker_confirm_remove_container, removal.name)
}

@Composable
fun DockerScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier) {
    val viewModel: DockerViewModel = androidx.lifecycle.viewmodel.compose.viewModel()
    val state = viewModel.state
    val available = state.owner?.capabilities?.contains(ServerCapabilities.DOCKER) == true
    var composer by mutableStateOf(false)
    var composeDraft by mutableStateOf("services:\n  app:\n    image: nginx:alpine\n")
    var destructive by mutableStateOf<Pair<DockerRemoval, () -> Unit>?>(null)
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> if (uri != null) viewModel.importCompose(uri) { yaml -> composeDraft = yaml; composer = true } }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.docker_title), onBack = onBack,
            trailing = { Row { TextButton(onClick = { picker.launch(arrayOf("text/yaml", "application/x-yaml", "text/plain")) }, enabled = available) { Text(stringResource(R.string.docker_import)) }
                TextButton(onClick = { composer = true }, enabled = available) { Text(stringResource(R.string.docker_new_stack)) }
                TextButton(onClick = viewModel::refresh, enabled = available && !state.loading) { Text(stringResource(R.string.common_refresh)) } } },
        )
        if (!available) { EmptyHint(stringResource(R.string.error_capability_missing)); return@Column }
        state.message?.let { message -> ErrorBanner(message.text(), viewModel::refresh, viewModel::dismissMessage, tone = message.tone) }
        if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        DockerStatusCard(state.status)
        LazyColumn(verticalArrangement = Arrangement.spacedBy(Spacing.md), modifier = Modifier.weight(1f)) {
            item { DockerStacks(state, viewModel, { destructive = DockerRemoval.Stack(it.name) to { viewModel.stackAction(it, "delete") } }) }
            item { DockerContainers(state.containers, { item, action -> if (action == "delete") destructive = DockerRemoval.Container(item.names) to { viewModel.containerAction(item, action) } else viewModel.containerAction(item, action) }) }
            item { SimpleList(stringResource(R.string.docker_images), state.images) { "${it.repository}:${it.tag} · ${it.size}" } }
            item { SimpleList(stringResource(R.string.docker_volumes), state.volumes) { "${it.name} · ${it.driver}" } }
            item { SimpleList(stringResource(R.string.docker_networks), state.networks) { "${it.name} · ${it.driver}" } }
        }
    }
    if (composer) DockerComposer(initialYaml = composeDraft, onDismiss = { composer = false }, onDeploy = { name, yaml -> viewModel.deploy(name, yaml); composer = false })
    destructive?.let { (removal, confirm) -> AlertDialog(onDismissRequest = { destructive = null }, title = { Text(stringResource(R.string.docker_confirm_title)) }, text = { Text(removalMessage(removal)) },
        confirmButton = { Button(onClick = { destructive = null; confirm() }) { Text(stringResource(R.string.common_delete)) } }, dismissButton = { TextButton(onClick = { destructive = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}

@Composable private fun DockerStatusCard(status: ApiResult<DockerStatus>?) = SectionCard(stringResource(R.string.docker_runtime)) {
    when (status) { is ApiResult.Success -> Text(if (status.value.available) stringResource(R.string.docker_ready, status.value.serverVersion) else stringResource(R.string.docker_unavailable))
        null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_unavailable), color = MaterialTheme.colorScheme.error) }
}

@Composable private fun DockerStacks(state: DockerScreenState, viewModel: DockerViewModel, onDelete: (DockerStack) -> Unit) = SectionCard(stringResource(R.string.docker_stacks)) {
    when (val stacks = state.stacks) { is ApiResult.Success -> if (stacks.value.isEmpty()) Text(stringResource(R.string.docker_empty_stacks)) else stacks.value.forEach { stack ->
        ListRow(stack.name, subtitle = stack.status, leading = { DesktopIcon(R.drawable.ic_app_docker, size = 22.dp) }, onClick = { viewModel.selectStack(stack) }, trailing = { Row { TextButton(onClick = { viewModel.stackAction(stack, "restart") }) { Text(stringResource(R.string.docker_restart)) }; TextButton(onClick = { onDelete(stack) }) { Text(stringResource(R.string.common_delete)) } } })
        if (state.selectedStack?.name == stack.name) StackServices(state.services)
    }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) }
}

@Composable private fun StackServices(services: ApiResult<List<DockerStackService>>?) { when (services) { is ApiResult.Success -> services.value.forEach { Text("${it.service} · ${it.state}", style = MaterialTheme.typography.bodySmall) }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) } }

@Composable private fun DockerContainers(result: ApiResult<List<DockerContainer>>?, action: (DockerContainer, String) -> Unit) = SectionCard(stringResource(R.string.docker_containers)) {
    when (result) { is ApiResult.Success -> if (result.value.isEmpty()) Text(stringResource(R.string.docker_empty_containers)) else result.value.forEach { item -> ListRow(item.names, subtitle = "${item.image} · ${item.status}", leading = { DesktopIcon(R.drawable.ic_app_docker, size = 22.dp) }, trailing = { Row { TextButton(onClick = { action(item, if (item.state.equals("running", true)) "stop" else "start") }) { Text(stringResource(if (item.state.equals("running", true)) R.string.docker_stop else R.string.docker_start)) }; TextButton(onClick = { action(item, "delete") }) { Text(stringResource(R.string.common_delete)) } } }) }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) }
}

@Composable private fun <T> SimpleList(title: String, result: ApiResult<List<T>>?, text: (T) -> String) = SectionCard(title) { when (result) { is ApiResult.Success -> if (result.value.isEmpty()) Text(stringResource(R.string.docker_empty_resources)) else result.value.forEach { Text(text(it)) }; null -> Text(stringResource(R.string.common_loading)); else -> Text(stringResource(R.string.docker_list_failed)) } }

@Composable @OptIn(ExperimentalMaterial3Api::class) private fun DockerComposer(initialYaml: String, onDismiss: () -> Unit, onDeploy: (String, String) -> Unit) {
    var name by mutableStateOf(""); var yaml by mutableStateOf(initialYaml)
    ModalBottomSheet(onDismissRequest = onDismiss) { Column(Modifier.fillMaxWidth().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        Text(stringResource(R.string.docker_new_stack), style = MaterialTheme.typography.headlineSmall); Text(stringResource(R.string.docker_compose_note), style = MaterialTheme.typography.bodySmall)
        OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.docker_stack_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(yaml, { yaml = it }, label = { Text(stringResource(R.string.docker_compose_yaml)) }, minLines = 8, modifier = Modifier.fillMaxWidth())
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }; Button(onClick = { onDeploy(name, yaml) }, enabled = name.isNotBlank() && yaml.isNotBlank()) { Text(stringResource(R.string.docker_deploy)) } }
    } }
}
