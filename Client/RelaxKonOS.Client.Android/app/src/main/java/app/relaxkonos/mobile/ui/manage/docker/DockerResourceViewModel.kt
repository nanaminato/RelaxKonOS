package app.relaxkonos.mobile.ui.manage.docker

import android.app.Application
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class DockerResourceState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: DockerResourceFacts? = null, val target: DockerResourceTarget? = null, val stats: DockerContainerStats? = null,
    val logs: DockerLogs? = null, val result: DockerOperation? = null, val pending: List<PendingDockerResource> = emptyList(),
    val blocked: String? = null, val problem: String? = null, val saved: Int = 0)
internal class DockerResourceViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var job: Job? = null
    private var generation = 0
    var state by mutableStateOf(DockerResourceState()); private set
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; state = DockerResourceState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = DockerResourceState(owner = state.owner) }
    fun refresh() = work { owner -> load(owner); state = state.copy(target = null, stats = null, logs = null, result = null) }
    fun select(kind: DockerResourceKind, id: String) = work { owner ->
        state = state.copy(target = null, stats = null, logs = null, result = null)
        val result = container.dockerResources.target(owner, kind, id); verify(owner)
        state = state.copy(target = (result as? ApiResult.Success)?.value, problem = problem(result))
        if (kind == DockerResourceKind.Containers && result is ApiResult.Success) readStats(owner, id)
    }
    fun pollStats() = work { owner -> state.target?.container?.let { readStats(owner, state.target!!.id) } }
    fun logs() = work { owner ->
        val target = state.target?.takeIf { it.kind == DockerResourceKind.Containers } ?: return@work
        val result = container.dockerResources.logs(owner, target.id); verify(owner)
        state = state.copy(logs = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    fun change(expected: DockerResourceFacts, target: DockerResourceTarget?, change: DockerResourceChange) = work { owner ->
        val result = container.dockerResources.change(owner, expected, target, change); verify(owner)
        load(owner)
        state = state.copy(target = null, stats = null, logs = null, result = (result as? ApiResult.Success)?.value,
            problem = problem(result) ?: state.problem,
            saved = state.saved + if (result is ApiResult.Success && result.value.success && state.pending.isEmpty()) 1 else 0)
    }
    fun accept(marker: PendingDockerResource) = work { owner ->
        val result = container.dockerResources.acceptFacts(owner, marker); verify(owner); load(owner)
        state = state.copy(target = null, stats = null, logs = null, result = null, problem = problem(result) ?: state.problem)
    }
    private suspend fun readStats(owner: SessionState.Active, id: String) {
        val result = container.dockerResources.stats(owner, id); verify(owner)
        state = state.copy(stats = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = container.dockerResources.facts(owner); verify(owner)
        val gate = if (owner.privilegedOperations) container.dockerMutationGate.check(owner) else ApiResult.Success(Unit); verify(owner)
        state = state.copy(facts = (result as? ApiResult.Success)?.value, pending = container.dockerResources.pending(owner),
            blocked = problem(gate), problem = problem(result))
    }
    private fun problem(result: ApiResult<*>): String? = when (result) {
        is ApiResult.Problem -> result.code; is ApiResult.Transport -> "docker.control.unverified"; is ApiResult.Success -> null
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.DOCKER !in owner.capabilities) return
        val request = generation; state = state.copy(busy = true, problem = null)
        job = viewModelScope.launch {
            try { block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, target = null, stats = null, logs = null, problem = "docker.control.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { container.dockerResources.pending(owner) }.getOrDefault(state.pending)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && container.session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (state.owner !== owner || container.session.state.value !== owner) throw CancellationException("Docker resource session changed")
    }
}
