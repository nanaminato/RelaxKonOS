package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

internal class CatalogUpdateEditor(
    private val session: AuthSession,
    private val deployments: DeploymentRepository,
    private val owner: SessionState.Active,
    initial: DeploymentApplication,
    private val templates: List<CatalogTemplate>,
    parentScope: CoroutineScope,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
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
    var unknown by mutableStateOf(deployments.hasUncertainRevision(owner, baseline.id))
    var reload by mutableIntStateOf(0)
    suspend fun refreshPreview() {
        preview = null
        val selected = target ?: return
        val result = try {
            withContext(ioDispatcher) { deployments.previewCatalogUpdate(owner, baseline.id, selected.version) }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { ApiResult.Transport(null) }
        if (session.state.value === owner) preview = result
    }
    fun submit(confirmed: CatalogApplicationUpdatePreview, onAccepted: (DeploymentOperation) -> Unit) {
        if (busy) return

        busy = true; result = null
        scope.launch {
            try {
                val submitted = withContext(ioDispatcher) { deployments.updateCatalog(owner, baseline, confirmed, UUID.randomUUID().toString()) }
                if (session.state.value !== owner) return@launch
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
                val current = withContext(ioDispatcher) { deployments.reconcileRevision(owner, baseline.id) }
                if (session.state.value !== owner) return@launch
                when (current) {
                    is ApiResult.Success -> {
                        if (current.value.application.id != baseline.id) { result = ApiResult.Transport(null); return@launch }
                        baseline = current.value.application; unknown = deployments.hasUncertainRevision(owner, baseline.id); result = null; reload++
                    }
                    is ApiResult.Problem -> result = current
                    is ApiResult.Transport -> result = current
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (session.state.value === owner) result = ApiResult.Transport(null) }
            finally { busy = false }
        }
    }
}
