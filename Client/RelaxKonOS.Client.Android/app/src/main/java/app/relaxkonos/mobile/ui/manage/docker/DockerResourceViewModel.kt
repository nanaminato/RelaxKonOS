package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.core.auth.AuthSession
import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class DockerResourceState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: DockerResourceFacts? = null, val target: DockerResourceTarget? = null, val stats: DockerContainerStats? = null,
    val logs: DockerLogs? = null, val result: DockerOperation? = null, val pending: List<PendingDockerResource> = emptyList(),
    val blocked: String? = null, val problem: String? = null, val saved: Int = 0, val feedbackVersion: Long = 0)
internal class ResourceConfirmation(val owner: SessionState.Active, val facts: DockerResourceFacts,
    val target: DockerResourceTarget?, val change: DockerResourceChange)
internal class DockerResourceViewModel(private val session: AuthSession, private val resources: DockerResourceRepository, private val gate: DockerMutationGate) : ViewModel() {
    var confirmation by mutableStateOf<ResourceConfirmation?>(null); private set
    fun dismissConfirmation() { confirmation = null }
    fun updateForce(review: ResourceConfirmation, force: Boolean) {
        if (confirmation !== review || review.change.action !in setOf(DockerResourceAction.DeleteContainer, DockerResourceAction.Stop)) return
        requestChange(review.owner, review.facts, review.target, review.change.copy(force = force))
    }
    fun requestChange(owner: SessionState.Active, facts: DockerResourceFacts, target: DockerResourceTarget?, change: DockerResourceChange) {
        if (session.state.value !== owner || state.owner !== owner || !owner.privilegedOperations || state.busy ||
            state.facts != facts || !facts.status.available || state.pending.isNotEmpty() || state.blocked != null ||
            change.target != null && (target == null || target != state.target)) return
        confirmation = ResourceConfirmation(owner, facts, target, change)
    }
    suspend fun observeStats(canObserve: () -> Boolean) {
        val owner = state.owner ?: return
        val id = state.target?.id ?: return
        var observedGeneration = generation
        fun eligible() = canObserve() && confirmation == null && state.owner === owner && session.state.value === owner && state.problem == null &&
            state.target?.let { it.id == id && it.container?.state == "running" } == true
        while (eligible()) {
            if (state.busy) {
                val pending = job ?: return
                pending.join()
                observedGeneration = generation
                continue
            }
            delay(3000)
            if (!eligible()) return
            if (state.busy || generation != observedGeneration) {
                observedGeneration = generation
                continue
            }
            pollStats()
            if (generation == observedGeneration) return
            val pending = job ?: return
            pending.join()
            observedGeneration = generation
        }
    }


    private var job: Job? = null
    private var generation = 0
    var state by mutableStateOf(DockerResourceState()); private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { dismissConfirmation(); job?.cancel(); generation++; state = DockerResourceState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { dismissConfirmation(); job?.cancel(); generation++; state = DockerResourceState(owner = state.owner) }
    fun refresh() = work { owner -> load(owner); state = state.copy(target = null, stats = null, logs = null) }
    fun select(kind: DockerResourceKind, id: String) = work { owner ->
        state = state.copy(target = null, stats = null, logs = null, result = null)
        val result = resources.target(owner, kind, id); verify(owner)
        state = state.copy(target = (result as? ApiResult.Success)?.value, problem = problem(result))
        if (kind == DockerResourceKind.Containers && result is ApiResult.Success) readStats(owner, id)
    }
    fun pollStats() = work { owner -> state.target?.container?.let { readStats(owner, state.target!!.id) } }
    fun logs() = work { owner ->
        val target = state.target?.takeIf { it.kind == DockerResourceKind.Containers } ?: return@work
        val result = resources.logs(owner, target.id); verify(owner)
        state = state.copy(logs = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    fun change(review: ResourceConfirmation) {
        if (confirmation !== review) return
        dismissConfirmation()
        if (session.state.value !== review.owner || state.owner !== review.owner || state.facts != review.facts ||
            review.target != null && state.target != review.target) return
        state = state.copy(result = null)
        work { owner ->
            val outcome = resources.change(owner, review.facts, review.target, review.change); verify(owner)
            val result = outcome.result
            val success = result is ApiResult.Success && result.value.success
            state = state.copy(target = null, stats = null, logs = null, result = (result as? ApiResult.Success)?.value,
                saved = state.saved + if (success) 1 else 0)
            showFacts(owner, outcome.facts)
            state = state.copy(problem = when {
                outcome.storageFailed -> "docker.control.recovery_storage_failed"
                problem(result) != null -> problem(result)
                success && (outcome.facts !is ApiResult.Success || !outcome.facts.value.status.available) -> "docker.control.refresh_failed"
                else -> state.problem
            })
        }
    }
    fun accept(marker: PendingDockerResource) = work { owner ->
        val result = resources.acceptFacts(owner, marker); verify(owner); showFacts(owner, result)
        state = state.copy(target = null, stats = null, logs = null, problem = problem(result) ?: state.problem)
    }
    private suspend fun readStats(owner: SessionState.Active, id: String) {
        val result = resources.stats(owner, id); verify(owner)
        state = state.copy(stats = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = resources.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private suspend fun showFacts(owner: SessionState.Active, result: ApiResult<DockerResourceFacts>) {
        val gateResult = try { if (owner.privilegedOperations) gate.check(owner) else ApiResult.Success(Unit) }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { ApiResult.Transport(null) }
        verify(owner)
        val pending = try { resources.pending(owner) }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) {
            state = state.copy(facts = null, blocked = "docker.control.recovery_storage_failed", problem = "docker.control.recovery_storage_failed")
            return
        }
        state = state.copy(facts = (result as? ApiResult.Success)?.value, pending = pending,
            blocked = problem(gateResult), problem = problem(result))
    }
    private fun problem(result: ApiResult<*>): String? = when (result) {
        is ApiResult.Problem -> result.code; is ApiResult.Transport -> "docker.control.unverified"; is ApiResult.Success -> null
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        dismissConfirmation()
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.DOCKER !in owner.capabilities) return
        val request = ++generation; state = state.copy(busy = true, problem = null, feedbackVersion = state.feedbackVersion + 1)
        job = viewModelScope.launch {
            try { block(owner) }
            catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(facts = null, target = null, stats = null, logs = null,
                    problem = "docker.control.unverified")
                throw cancelled
            }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, target = null, stats = null, logs = null, problem = "docker.control.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { resources.pending(owner) }.getOrDefault(state.pending)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (state.owner !== owner || session.state.value !== owner) throw CancellationException("Docker resource session changed")
    }
}
