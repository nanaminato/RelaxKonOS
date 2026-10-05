package app.relaxkonos.mobile.ui.manage.docker


import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import android.app.Application
import android.net.Uri
import androidx.compose.runtime.mutableStateOf
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
import app.relaxkonos.mobile.core.net.DockerStackOperationState
import app.relaxkonos.mobile.core.net.DockerStackPreview
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.DockerVolume
import app.relaxkonos.mobile.core.net.DockerVolumeDetails
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
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
