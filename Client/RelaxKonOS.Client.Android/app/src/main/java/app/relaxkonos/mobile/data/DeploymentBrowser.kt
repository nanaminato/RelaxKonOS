package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import java.util.UUID

data class DeploymentBrowserState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val applications: ApiResult<List<DeploymentApplication>>? = null,
    val runtime: ApiResult<DeploymentRuntime>? = null,
    val templates: ApiResult<List<DeploymentTemplate>>? = null,
    val checkedAtMillis: Long? = null,
    val selectedId: String? = null,
    val detailLoading: Boolean = false,
    val detail: ApiResult<DeploymentSnapshot>? = null,
    val detailCheckedAtMillis: Long? = null,
    val logsLoading: Boolean = false,
    val logs: ApiResult<DeploymentLog>? = null,
    val submitting: Boolean = false,
    val submission: ApiResult<DeploymentOperation>? = null,
)

/** A session-scoped, read-only browser. Failed refreshes discard old runtime claims. */
class DeploymentBrowser(
    private val repository: DeploymentRepository,
    private val session: AuthSession,
    private val scope: CoroutineScope,
) {
    private val mutableState = MutableStateFlow(DeploymentBrowserState())
    val state = mutableState.asStateFlow()
    private var listJob: Job? = null
    private var detailJob: Job? = null
    private var operationJob: Job? = null
    private var listGeneration = 0
    private var detailGeneration = 0

    init {
        scope.launch {
            session.state.collect { value ->
                listJob?.cancel()
                detailJob?.cancel()
                operationJob?.cancel()
                listGeneration++
                detailGeneration++
                mutableState.value = DeploymentBrowserState(owner = value as? SessionState.Active)
                refresh()
            }
        }
    }

    fun refresh() {
        val owner = mutableState.value.owner ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(loading = true, applications = null, runtime = null, templates = null, checkedAtMillis = null) }
        listJob = scope.launch {
            try {
                val runtime = if (ServerCapabilities.DOCKER in owner.capabilities) repository.runtime(owner) else null
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(runtime = runtime) }
                val templates = repository.templates(owner)
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(templates = templates) }
                val applications = repository.applications(owner)
                if (current(owner) && generation == listGeneration) mutableState.update {
                    it.copy(applications = applications, checkedAtMillis = if (applications is ApiResult.Success) System.currentTimeMillis() else null)
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(loading = false) }
            }
        }
        mutableState.value.selectedId?.let(::select)
    }

    fun select(id: String?) {
        val owner = mutableState.value.owner ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        detailJob?.cancel()
        operationJob?.cancel()
        val generation = ++detailGeneration
        mutableState.update { it.copy(selectedId = id, detail = null, detailLoading = id != null, detailCheckedAtMillis = null, logsLoading = id != null, logs = null) }
        if (id == null) return
        detailJob = scope.launch {
            try {
                val result = repository.snapshot(owner, id)
                if (current(owner) && generation == detailGeneration) mutableState.update {
                    it.copy(detail = result, detailCheckedAtMillis = if (result is ApiResult.Success) System.currentTimeMillis() else null)
                }
                if (result is ApiResult.Success) {
                    val logs = repository.logs(owner, id)
                    if (current(owner) && generation == detailGeneration) mutableState.update { it.copy(logs = logs) }
                }
            } finally {
                if (current(owner) && generation == detailGeneration) mutableState.update { it.copy(detailLoading = false, logsLoading = false) }
            }
        }
    }

    /** Starts the constrained image path. Archives, secrets and arbitrary host mounts remain out of this flow. */
    fun createImage(name: String, imageReference: String, containerPort: Int, configuration: List<DeploymentConfigEntry> = emptyList()) {
        val owner = mutableState.value.owner ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.createAndDeployImage(
                    owner = owner,
                    definition = ImageDeploymentDefinition(name = name, containerPort = containerPort, configuration = configuration),
                    imageReference = imageReference,
                    definitionKey = UUID.randomUUID().toString(),
                    deploymentKey = UUID.randomUUID().toString(),
                )
                if (current(owner) && generation == listGeneration) {
                    mutableState.update {
                        it.copy(
                            submitting = false,
                            submission = result,
                            selectedId = (result as? ApiResult.Success)?.value?.applicationId ?: it.selectedId,
                        )
                    }
                    if (result is ApiResult.Success) {
                        refresh()
                        observeOperation(owner, result.value)
                    }
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    /** Creates an archive-template definition, streams the selected SAF document, then queues the resulting revision. */
    fun createArchive(definition: ArchiveDeploymentDefinition, archive: PickedDocument) {
        val owner = mutableState.value.owner ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.createAndDeployArchive(
                    owner, definition, archive, UUID.randomUUID().toString(), UUID.randomUUID().toString(),
                )
                if (current(owner) && generation == listGeneration) {
                    mutableState.update {
                        it.copy(
                            submitting = false,
                            submission = result,
                            selectedId = (result as? ApiResult.Success)?.value?.applicationId ?: it.selectedId,
                        )
                    }
                    if (result is ApiResult.Success) {
                        refresh()
                        observeOperation(owner, result.value)
                    }
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    fun archiveUnavailable() {
        mutableState.update { it.copy(submitting = false, submission = ApiResult.Transport("Selected deployment archive cannot be read.")) }
    }

    fun lifecycle(action: DeploymentLifecycleAction) {
        val owner = mutableState.value.owner ?: return
        val applicationId = mutableState.value.selectedId ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.lifecycle(owner, applicationId, action, UUID.randomUUID().toString())
                if (current(owner) && generation == listGeneration) {
                    mutableState.update { it.copy(submitting = false, submission = result) }
                    if (result is ApiResult.Success) {
                        refresh()
                        observeOperation(owner, result.value)
                    }
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    /** Reuses a server-recorded immutable revision; the server retains volume data independently. */
    fun rollback(revision: DeploymentRevision) {
        val owner = mutableState.value.owner ?: return
        val applicationId = mutableState.value.selectedId ?: return
        if (revision.isCurrent || ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.rollback(owner, applicationId, revision.id, UUID.randomUUID().toString())
                if (current(owner) && generation == listGeneration) {
                    mutableState.update { it.copy(submitting = false, submission = result) }
                    if (result is ApiResult.Success) {
                        refresh()
                        observeOperation(owner, result.value)
                    }
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    fun delete() {
        val owner = mutableState.value.owner ?: return
        val applicationId = mutableState.value.selectedId ?: return
        if (ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.delete(owner, applicationId, UUID.randomUUID().toString())
                if (current(owner) && generation == listGeneration) {
                    mutableState.update { it.copy(submitting = false, submission = result, selectedId = if (result is ApiResult.Success) null else it.selectedId) }
                    if (result is ApiResult.Success) refresh()
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    fun cancel(operation: DeploymentOperation) {
        val owner = mutableState.value.owner ?: return
        if (!operation.cancellable || ServerCapabilities.APPLICATION_DEPLOYMENTS !in owner.capabilities) return
        listJob?.cancel()
        val generation = ++listGeneration
        mutableState.update { it.copy(submitting = true, submission = null) }
        listJob = scope.launch {
            try {
                val result = repository.cancel(owner, operation.operationId, UUID.randomUUID().toString())
                if (current(owner) && generation == listGeneration) {
                    mutableState.update { it.copy(submitting = false, submission = result) }
                    if (result is ApiResult.Success) {
                        refresh()
                        observeOperation(owner, result.value)
                    }
                }
            } finally {
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(submitting = false) }
            }
        }
    }

    /** Polls the durable record only while the server still reports this operation as active. */
    private fun observeOperation(owner: SessionState.Active, operation: DeploymentOperation) {
        operationJob?.cancel()
        operationJob = scope.launch {
            while (isActive && current(owner) && mutableState.value.selectedId == operation.applicationId) {
                delay(OPERATION_POLL_MILLIS)
                val generation = ++detailGeneration
                val snapshot = repository.snapshot(owner, operation.applicationId)
                if (!current(owner) || generation != detailGeneration || mutableState.value.selectedId != operation.applicationId) break
                if (snapshot is ApiResult.Success) {
                    mutableState.update { it.copy(detail = snapshot, detailCheckedAtMillis = System.currentTimeMillis()) }
                    val currentOperation = snapshot.value.activeOperation?.takeIf { it.operationId == operation.operationId }
                        ?: snapshot.value.operations.firstOrNull { it.operationId == operation.operationId }
                    if (currentOperation == null || currentOperation.state !in ACTIVE_OPERATION_STATES) break
                } else {
                    // A failed poll leaves the last authoritative state visible; manual refresh can retry.
                    break
                }
            }
        }
    }

    private fun current(owner: SessionState.Active): Boolean = session.state.value === owner && mutableState.value.owner === owner

    private companion object {
        const val OPERATION_POLL_MILLIS = 1_500L
        val ACTIVE_OPERATION_STATES = setOf("Queued", "Running")
    }
}
