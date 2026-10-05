package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

internal class CatalogUpdateEditor(
    private val container: AppContainer,
    private val owner: SessionState.Active,
    initial: DeploymentApplication,
    private val templates: List<CatalogTemplate>,
    parentScope: CoroutineScope,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    fun close() { job.cancel() }
    var baseline by mutableStateOf(initial)
    val targets get() = templates.filter { it.id == baseline.catalogTemplateId }
    var selectedVersion by mutableStateOf(targets.firstOrNull()?.version)
    val target get() = targets.firstOrNull { it.version == selectedVersion }
    var preview by mutableStateOf<ApiResult<CatalogApplicationUpdatePreview>?>(null)
    var result by mutableStateOf<ApiResult<DeploymentOperation>?>(null)
    var busy by mutableStateOf(false)
    var unknown by mutableStateOf(container.deployments.hasUncertainRevision(owner, baseline.id))
    var reload by mutableIntStateOf(0)
    suspend fun refreshPreview() {
        preview = null
        val selected = target ?: return
        val result = withContext(Dispatchers.IO) { container.deployments.previewCatalogUpdate(owner, baseline.id, selected.version) }
        if (container.session.state.value === owner) preview = result
    }
    fun submit(confirmed: CatalogApplicationUpdatePreview, onAccepted: (DeploymentOperation) -> Unit) {
        if (busy) return

        busy = true; result = null
        scope.launch {
            try {
                val submitted = withContext(Dispatchers.IO) { container.deployments.updateCatalog(owner, baseline, confirmed, UUID.randomUUID().toString()) }
                if (container.session.state.value !== owner) return@launch
                result = submitted.result; unknown = submitted.mayHaveQueued
                if (submitted.result is ApiResult.Success) onAccepted(submitted.result.value)
            } finally { busy = false }
        }

    }
    fun readCurrent() {
        if (busy) return

        busy = true
        scope.launch {
            try {
                when (val current = withContext(Dispatchers.IO) { container.deployments.reconcileRevision(owner, baseline.id) }) {
                    is ApiResult.Success -> {
                        if (current.value.application.id != baseline.id) { result = ApiResult.Transport(null); return@launch }
                        baseline = current.value.application; unknown = container.deployments.hasUncertainRevision(owner, baseline.id); result = null; reload++
                    }
                    is ApiResult.Problem -> result = current
                    is ApiResult.Transport -> result = current
                }
            } finally { busy = false }
        }

    }
}
