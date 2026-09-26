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

data class DeploymentBrowserState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val applications: ApiResult<List<DeploymentApplication>>? = null,
    val runtime: ApiResult<DeploymentRuntime>? = null,
    val checkedAtMillis: Long? = null,
    val selectedId: String? = null,
    val detailLoading: Boolean = false,
    val detail: ApiResult<DeploymentSnapshot>? = null,
    val detailCheckedAtMillis: Long? = null,
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
    private var listGeneration = 0
    private var detailGeneration = 0

    init {
        scope.launch {
            session.state.collect { value ->
                listJob?.cancel()
                detailJob?.cancel()
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
        mutableState.update { it.copy(loading = true, applications = null, runtime = null, checkedAtMillis = null) }
        listJob = scope.launch {
            try {
                val runtime = if (ServerCapabilities.DOCKER in owner.capabilities) repository.runtime(owner) else null
                if (current(owner) && generation == listGeneration) mutableState.update { it.copy(runtime = runtime) }
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
        val generation = ++detailGeneration
        mutableState.update { it.copy(selectedId = id, detail = null, detailLoading = id != null, detailCheckedAtMillis = null) }
        if (id == null) return
        detailJob = scope.launch {
            try {
                val result = repository.snapshot(owner, id)
                if (current(owner) && generation == detailGeneration) mutableState.update {
                    it.copy(detail = result, detailCheckedAtMillis = if (result is ApiResult.Success) System.currentTimeMillis() else null)
                }
            } finally {
                if (current(owner) && generation == detailGeneration) mutableState.update { it.copy(detailLoading = false) }
            }
        }
    }

    private fun current(owner: SessionState.Active): Boolean = session.state.value === owner && mutableState.value.owner === owner
}
