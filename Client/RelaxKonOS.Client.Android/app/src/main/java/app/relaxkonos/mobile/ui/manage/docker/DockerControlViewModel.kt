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
import kotlinx.coroutines.sync.withLock

internal data class DockerControlState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: DockerControlFacts? = null, val pending: List<PendingDockerControl> = emptyList(),
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false,
    val resourcePending: Boolean = false, val checkedAtMillis: Long? = null, val problem: String? = null, val saved: Int = 0)
internal class DockerControlViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var job: Job? = null
    private var generation = 0
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(DockerControlState()); private set
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; intent = null; state = DockerControlState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false, facts = null, installationVerified = false) }
    fun refresh() = work { owner ->
        load(owner)
        if (owner.privilegedOperations) {
            val known = container.installations.pending(owner).firstOrNull { it.service == InstallationService.Docker && it.operationId != null }
            if (known != null) {
                val result = container.installations.recover(owner, known); verify(owner)
                if (result is ApiResult.Success && result.value != null) installResult(owner, ApiResult.Success(result.value))
                else state = state.copy(installationVerified = false)
                return@work
            }
            val active = container.installations.active(owner, InstallationService.Docker); verify(owner)
            if (active is ApiResult.Success && active.value != null) installResult(owner, ApiResult.Success(active.value))
            else if (active is ApiResult.Success) {
                val id = state.installation?.operationId ?: container.operationIndex.forOwner(owner).firstOrNull {
                    it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Docker.name }?.operationId
                if (id != null) installResult(owner, container.installations.operation(owner, id))
            } else state = state.copy(installationVerified = false)
        }
    }
    fun change(expected: DockerControlFacts, change: DockerControlChange) = work { owner ->
        require(!state.pendingInstallation && state.installation?.state?.active != true && (state.installation == null || state.installationVerified))
        val result = container.dockerControl.change(owner, expected, change); verify(owner)
        load(owner)
        state = state.copy(problem = problem(result) ?: state.problem, saved = state.saved + if (result is ApiResult.Success) 1 else 0)
    }
    fun accept(pending: PendingDockerControl) = work { owner ->
        val result = container.dockerControl.acceptFacts(owner, pending); verify(owner); load(owner)
        state = state.copy(problem = problem(result) ?: state.problem)
    }
    fun install() = work { owner ->
        require(owner.privilegedOperations && owner.serverPlatform.equals("linux", true) && state.pending.isEmpty() && !state.resourcePending)
        require(state.pendingInstallation || state.facts?.status?.let { !it.available && it.problemCode == "docker.not_installed" } == true)
        if (intent == null) intent = container.installations.prepare(owner, InstallationKind.Install, DockerInstallationRequest(true))
        val result = container.dockerMutationGate.mutex.withLock {
            require(container.dockerControl.pending(owner).isEmpty() && container.dockerResources.pending(owner).isEmpty())
            container.installations.submit(requireNotNull(intent), container.elevationAnswers)
        }; verify(owner)
        installResult(owner, result)
        if (result is ApiResult.Success || result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun pollInstall() = work { owner -> state.installation?.operationId?.let { installResult(owner, container.installations.operation(owner, it)) } }
    fun cancelInstall() = work { owner ->
        val operation = state.installation ?: return@work
        require(state.installationVerified && operation.state.active && operation.cancellable)
        installResult(owner, container.installations.cancel(owner, operation.operationId))
    }
    fun recoverInstall(id: String, identified: Boolean) = work { owner ->
        val pending = container.installations.pending(owner).firstOrNull { it.service == InstallationService.Docker }
        if (pending != null && !identified) return@work
        val result = if (pending == null) container.installations.recoverById(owner, id.trim())
            else container.installations.identifyOriginal(owner, pending, id.trim())
        installResult(owner, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) intent = null
    }
    private suspend fun installResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(owner)
        state = state.copy(pendingInstallation = container.installations.pending(owner).any { it.service == InstallationService.Docker })
        if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) {
            state = state.copy(installation = result.value, installationVerified = true, problem = result.value.problemCode)
            if (!result.value.state.active) load(owner)
        } else state = state.copy(installationVerified = false, problem = problem(result) ?: "docker.control.unverified")
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = container.dockerControl.facts(owner); verify(owner)
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = problem(result), checkedAtMillis = System.currentTimeMillis(),
            resourcePending = container.dockerResources.pending(owner).isNotEmpty(), pending = container.dockerControl.pending(owner), pendingInstallation = container.installations.pending(owner).any { it.service == InstallationService.Docker })
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
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, installationVerified = false, problem = "docker.control.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { container.dockerControl.pending(owner) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { container.installations.pending(owner).any { it.service == InstallationService.Docker } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && container.session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (state.owner !== owner || container.session.state.value !== owner) throw CancellationException("Docker control session changed")
    }
}
