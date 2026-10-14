package app.relaxkonos.mobile.ui.manage.docker


import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import java.io.InputStream
import android.net.Uri
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.DockerRepository
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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.Job
import app.relaxkonos.mobile.data.PendingDockerCompose
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
    val pendingRequests: List<PendingDockerCompose> = emptyList(),
    val pendingUnreadable: Boolean = false,
    val recoveredOperation: DockerStackOperation? = null,
)

class DockerViewModel(private val session: AuthSession, private val repository: DockerRepository, private val openDocument: (Uri) -> InputStream?) : ViewModel() {
    var state by mutableStateOf(DockerScreenState(owner = (session.state.value as? SessionState.Active)))
        private set
    private var refreshJob: Job? = null
    private var refreshGeneration = 0
    val canSubmit: Boolean get() = state.owner?.privilegedOperations == true && session.state.value === state.owner &&
        !state.busy && !state.pendingUnreadable && state.pendingRequests.isEmpty()

    private fun syncPending(owner: SessionState.Active) {
        if (session.state.value !== owner) return
        try { state = state.copy(pendingRequests = repository.pendingRequests(owner), pendingUnreadable = false) }
        catch (_: Exception) { state = state.copy(pendingUnreadable = true) }
    }

    fun recover(record: PendingDockerCompose) {
        val owner = state.owner ?: return
        if (state.busy || record !in state.pendingRequests) return
        state = state.copy(busy = true, message = null)
        viewModelScope.launch {
            try {
                val result = repository.recoverRequest(owner, record)
                if (session.state.value !== owner) return@launch
                syncPending(owner)
                if (result is ApiResult.Success) {
                    state = state.copy(recoveredOperation = result.value, selectedStack = DockerStack(result.value.projectName, "", "", ""),
                        operations = ApiResult.Success(listOf(result.value)), message = operationMessage(result.value))
                    if (result.value.state.active) track(owner, result.value)
                } else state = state.copy(message = UiMessage(R.string.docker_stack_lookup_unresolved))
            } catch (cancelled: CancellationException) {
                if (session.state.value === owner) state = state.copy(message = UiMessage(R.string.docker_stack_lookup_unresolved))
                throw cancelled
            } catch (_: Exception) {
                if (session.state.value === owner) state = state.copy(message = UiMessage(R.string.docker_stack_lookup_unresolved))
            } finally {
                if (session.state.value === owner) { syncPending(owner); state = state.copy(busy = false) }
            }
        }
    }

    private suspend fun guardedWrite(owner: SessionState.Active, block: suspend () -> Unit) {
        try { block() }
        catch (cancelled: CancellationException) {
            if (session.state.value === owner) { syncPending(owner); state = state.copy(message = writeFailure(R.string.error_connectivity)) }
            throw cancelled
        } catch (_: Exception) {
            if (session.state.value === owner) { syncPending(owner); state = state.copy(message = writeFailure(R.string.error_generic)) }
        } finally {
            if (session.state.value === owner) { syncPending(owner); state = state.copy(busy = false) }
        }
    }
    private fun writeFailure(fallback: Int) = UiMessage(if (state.pendingUnreadable || state.pendingRequests.isNotEmpty()) R.string.docker_stack_result_unknown else fallback)

    init {
        viewModelScope.launch {
            session.state.collect {
                refreshGeneration++
                refreshJob?.cancel()
                state = DockerScreenState(owner = (session.state.value as? SessionState.Active))
                state.owner?.let(::syncPending)
                refresh()
            }
        }
    }

    fun refresh() {
        val owner = (session.state.value as? SessionState.Active) ?: return
        syncPending(owner)
        if (ServerCapabilities.DOCKER !in owner.capabilities || state.loading) return
        val generation = ++refreshGeneration
        state = state.copy(owner = owner, loading = true, message = null)
        refreshJob = viewModelScope.launch {
            fun current() = session.state.value === owner && refreshGeneration == generation
            suspend fun <T> read(action: suspend () -> ApiResult<T>): ApiResult<T> {
                if (!current()) throw CancellationException("Docker refresh superseded")
                return try { action() }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) }
            }
            try {
            // Every result is independent: an unavailable image listing must not erase the stack
            // list that lets a user diagnose it.
            val status = read { repository.status(owner) }
            val containers = read { repository.containers(owner) }
            val images = read { repository.images(owner) }
            val networks = read { repository.networks(owner) }
            val volumes = read { repository.volumes(owner) }
            val stacks = read { repository.stacks(owner) }
            if (current()) state = state.copy(
                loading = false, status = status, containers = containers, images = images,
                networks = networks, volumes = volumes, stacks = stacks,
                message = if (listOf(status, containers, images, networks, volumes, stacks).any { it is ApiResult.Transport })
                    UiMessage(R.string.error_connectivity) else null,
            )
            } catch (cancelled: CancellationException) {
                if (current()) state = state.copy(status = ApiResult.Transport(null), containers = ApiResult.Transport(null), images = ApiResult.Transport(null),
                    networks = ApiResult.Transport(null), volumes = ApiResult.Transport(null), stacks = ApiResult.Transport(null), message = UiMessage(R.string.error_connectivity))
                throw cancelled
            } finally { if (current()) state = state.copy(loading = false) }
        }
    }

    fun selectStack(stack: DockerStack) {
        val owner = state.owner ?: return
        state = state.copy(selectedStack = stack, services = null, operations = null, diagnostics = null, logs = null)
        viewModelScope.launch {
            val services = repository.services(owner, stack.name)
            val operations = repository.stackOperations(owner, stack.name)
            if ((session.state.value as? SessionState.Active) === owner && state.selectedStack?.name == stack.name) {
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
            try {
            val result = repository.previewStack(owner, name.trim(), yaml)
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            state = state.copy(busy = false, preview = (result as? ApiResult.Success)?.value,
                message = when (result) {
                    is ApiResult.Success -> UiMessage(R.string.docker_stack_preview_ready, listOf(result.value.services.size, result.value.volumes.size, result.value.networks.size), tone = StatusTone.Success)
                    // A refusal is reported as the server's verdict. The screen never rewrites a
                    // rejected definition into something else, and it never claims a cause it did
                    // not verify.
                    else -> result.dockerFailure()
                })
            } catch (cancelled: CancellationException) {
                if (session.state.value === owner) state = state.copy(preview = null, message = UiMessage(R.string.error_connectivity))
                throw cancelled
            } catch (_: Exception) {
                if (session.state.value === owner) state = state.copy(preview = null, message = UiMessage(R.string.error_generic))
            } finally {
                if (session.state.value === owner) state = state.copy(busy = false)
            }
        }
    }

    fun clearPreview() { state = state.copy(preview = null) }

    /**
     * Deploys the approved definition. The preview is taken here, immediately before the submission,
     * so the version the server verifies is the version of the document that is being sent — not an
     * answer from an earlier click that the operator may have edited away.
     */
    fun deploy(name: String, yaml: String, onSubmitted: () -> Unit) {
        val owner = state.owner ?: return
        syncPending(owner)
        if (!canSubmit) return
        if (state.busy || name.isBlank() || yaml.isBlank()) { state = state.copy(message = UiMessage(R.string.docker_stack_required)); return }
        state = state.copy(busy = true, message = null)
        viewModelScope.launch {
            val preview = try {
                repository.previewStack(owner, name.trim(), yaml)
            } catch (cancelled: CancellationException) {
                if (session.state.value === owner) state = state.copy(busy = false, preview = null, message = UiMessage(R.string.error_connectivity))
                throw cancelled
            } catch (_: Exception) {
                if (session.state.value === owner) state = state.copy(busy = false, preview = null, message = UiMessage(R.string.error_generic))
                return@launch
            }
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            if (preview !is ApiResult.Success) {
                // Nothing was submitted: the deployment never left the device.
                if ((session.state.value as? SessionState.Active) === owner) state = state.copy(busy = false, message = preview.dockerFailure())
                return@launch
            }
            guardedWrite(owner) {
            val submitted = repository.deployStack(owner, name.trim(), yaml, preview.value.definitionVersion)
            if (session.state.value !== owner) return@guardedWrite
            syncPending(owner)
            state = state.copy(busy = false, preview = preview.value)
            when (submitted) {
                is ApiResult.Success -> {
                    state = state.copy(message = UiMessage(R.string.docker_stack_submitted, tone = StatusTone.Success))
                    onSubmitted()
                    track(owner, submitted.value)
                }
                else -> state = state.copy(message = if (state.pendingRequests.isNotEmpty() || state.pendingUnreadable) writeFailure(R.string.error_connectivity) else submitted.dockerFailure())
            }
            }
        }
    }

    fun stackAction(stack: DockerStack, action: String, confirmed: Boolean) {
        val owner = state.owner ?: return
        syncPending(owner)
        if (!canSubmit) return
        if (state.busy) return
        state = state.copy(busy = true, message = null)
        viewModelScope.launch {
            guardedWrite(owner) {
            val result = repository.stackAction(owner, stack.name, action, confirmed)
            if (session.state.value !== owner) return@guardedWrite
            syncPending(owner)
            state = state.copy(busy = false)
            when (result) {
                is ApiResult.Success -> { state = state.copy(message = UiMessage(R.string.docker_stack_submitted, tone = StatusTone.Success)); track(owner, result.value) }
                else -> state = state.copy(message = if (state.pendingRequests.isNotEmpty() || state.pendingUnreadable) writeFailure(R.string.error_connectivity) else result.dockerFailure())
            }
            }
        }
    }

    fun cancelOperation(operationId: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = repository.cancelStackOperation(owner, operationId)
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            state = state.copy(message = when (result) {
                is ApiResult.Success -> UiMessage(R.string.docker_stack_cancel_requested, tone = StatusTone.Neutral)
                else -> result.dockerFailure()
            })
        }
    }

    fun loadLogs(id: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = repository.logs(owner, id, 200)
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            if (state.logs?.first == id) state = state.copy(logs = null)
            else if (result is ApiResult.Success) state = state.copy(logs = id to result.value)
            else state = state.copy(message = result.dockerFailure())
        }
    }

    /** Reads the reference list behind a volume. Deleting an in-use volume is refused by the server. */
    fun loadVolume(name: String) {
        val owner = state.owner ?: return
        viewModelScope.launch {
            val result = repository.volumeDetails(owner, name)
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            if (state.volume?.first == name) state = state.copy(volume = null)
            else if (result is ApiResult.Success) state = state.copy(volume = name to result.value)
            else state = state.copy(message = result.dockerFailure())
        }
    }

    fun importCompose(uri: Uri, onImported: (String) -> Unit) {
        val owner = (session.state.value as? SessionState.Active) ?: return
        viewModelScope.launch {
            try {
                val source = withContext(Dispatchers.IO) {
                    openDocument(uri)?.use(::readComposeDocument)
                        ?: error("unreadable document")
                }
                if ((session.state.value as? SessionState.Active) === owner) onImported(source)
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                if ((session.state.value as? SessionState.Active) === owner) state = state.copy(message = UiMessage(
                    if (error is ComposeDocumentTooLargeException) R.string.docker_import_too_large else R.string.docker_import_failed))
            }
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
                val next = repository.stackOperation(owner, current.operationId)
                if (next is ApiResult.Success) current = next.value else break
            }
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            val diagnostics = (repository.stackOperationDiagnostics(owner, current.operationId) as? ApiResult.Success)?.value
            state = state.copy(diagnostics = diagnostics, message = operationMessage(current),
                recoveredOperation = if (state.recoveredOperation?.operationId == current.operationId) current else state.recoveredOperation)
            if (state.selectedStack?.name == current.projectName) {
                val services = repository.services(owner, current.projectName)
                val operations = repository.stackOperations(owner, current.projectName)
                if ((session.state.value as? SessionState.Active) === owner && state.selectedStack?.name == current.projectName)
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
