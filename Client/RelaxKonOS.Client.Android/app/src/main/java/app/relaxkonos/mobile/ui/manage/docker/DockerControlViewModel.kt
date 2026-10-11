package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.core.auth.AuthSession
import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.withLock

internal data class DockerControlState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: DockerControlFacts? = null, val pending: List<PendingDockerControl> = emptyList(),
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false,
    val resourcePending: Boolean = false, val checkedAtMillis: Long? = null, val problem: String? = null, val saved: Int = 0,
    val installationLookupId: String? = null, val installationLookupMissing: Boolean = false, val installationLookupFailed: Boolean = false)
/** A missing historical receipt must not invalidate the independently verified engine facts. */
internal fun DockerControlState.installationLookupFailure(id: String?, result: ApiResult<*>): DockerControlState = copy(
    installationVerified = false,
    installationLookupId = id,
    installationLookupMissing = result is ApiResult.Problem && result.status == 404,
    installationLookupFailed = true,
)

internal class DockerControlViewModel(private val session: AuthSession, private val control: DockerControlRepository,
    private val resources: DockerResourceRepository, private val installations: InstallationRepository,
    private val operationIndex: OperationIndex, private val mutationGate: DockerMutationGate,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    suspend fun observeInstallation() {
        if (state.installationVerified && state.installation?.state?.active == true && !state.busy) {
            kotlinx.coroutines.delay(1500)
            pollInstall()
        }
    }


    private var job: Job? = null
    private var generation = 0
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(DockerControlState()); private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; intent = null; state = DockerControlState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false, facts = null, installationVerified = false) }
    fun refresh() = work { owner ->
        load(owner)
        if (owner.privilegedOperations) {
            val known = installations.pending(owner).firstOrNull { it.service == InstallationService.Docker && it.operationId != null }
            if (known != null) {
                val result = installations.recover(owner, known); verify(owner)
                if (result is ApiResult.Success && result.value != null) installResult(owner, ApiResult.Success(result.value), observing = true)
                else state = state.installationLookupFailure(known.operationId, result)
                return@work
            }
            val active = installations.active(owner, InstallationService.Docker); verify(owner)
            if (active is ApiResult.Success && active.value != null) installResult(owner, ApiResult.Success(active.value), observing = true)
            else if (active is ApiResult.Success) {
                val id = state.installation?.operationId ?: operationIndex.forOwner(owner).firstOrNull {
                    it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Docker.name }?.operationId
                if (id != null) {
                    val result = installations.operation(owner, id); verify(owner)
                    if (result is ApiResult.Success) installResult(owner, result, observing = true)
                    else state = state.installationLookupFailure(id, result)
                }
            } else state = state.installationLookupFailure(state.installationLookupId, active)
        }
    }
    fun change(expected: DockerControlFacts, change: DockerControlChange) = work { owner ->
        require(!state.pendingInstallation && state.installation?.state?.active != true && (state.installation == null || state.installationVerified))
        val result = control.change(owner, expected, change); verify(owner)
        load(owner)
        state = state.copy(problem = problem(result) ?: state.problem, saved = state.saved + if (result is ApiResult.Success) 1 else 0)
    }
    fun accept(pending: PendingDockerControl) = work { owner ->
        val result = control.acceptFacts(owner, pending); verify(owner); showFacts(owner, result)
    }
    fun install() = work { owner ->
        require(owner.privilegedOperations && owner.serverPlatform.equals("linux", true) && state.pending.isEmpty() && !state.resourcePending)
        require(state.pendingInstallation || state.facts?.status?.let { !it.available && it.problemCode == "docker.not_installed" } == true)
        if (intent == null) intent = installations.prepare(owner, InstallationKind.Install, DockerInstallationRequest(true))
        val result = mutationGate.mutex.withLock {
            require(control.pending(owner).isEmpty() && resources.pending(owner).isEmpty())
            installations.submit(requireNotNull(intent), elevationAnswers)
        }; verify(owner)
        installResult(owner, result)
        if (result is ApiResult.Success || result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun pollInstall() = work { owner -> state.installation?.operationId?.let { installResult(owner, installations.operation(owner, it)) } }
    fun cancelInstall() = work { owner ->
        val operation = state.installation ?: return@work
        require(state.installationVerified && operation.state.active && operation.cancellable)
        installResult(owner, installations.cancel(owner, operation.operationId))
    }
    fun recoverInstall(id: String, identified: Boolean) = work { owner ->
        val pending = installations.pending(owner).firstOrNull { it.service == InstallationService.Docker }
        if (pending != null && !identified) return@work
        val result = if (pending == null) installations.recoverById(owner, id.trim())
            else installations.identifyOriginal(owner, pending, id.trim())
        installResult(owner, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) intent = null
    }
    private suspend fun installResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>, observing: Boolean = false) {
        verify(owner)
        state = state.copy(pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Docker })
        if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) {
            state = state.copy(installation = result.value, installationVerified = true,
                installationLookupId = result.value.operationId, installationLookupMissing = false, installationLookupFailed = false,
                problem = if (observing) state.problem else result.value.problemCode)
            if (!observing && !result.value.state.active) load(owner)
        } else state = state.copy(installationVerified = false, problem = problem(result) ?: "docker.control.unverified")
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = control.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private fun showFacts(owner: SessionState.Active, result: ApiResult<DockerControlFacts>) {
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = problem(result), checkedAtMillis = System.currentTimeMillis(),
            resourcePending = resources.pending(owner).isNotEmpty(), pending = control.pending(owner), pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Docker })
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
            catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(facts = null, installationVerified = false,
                    problem = "docker.control.unverified")
                throw cancelled
            }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, installationVerified = false, problem = "docker.control.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { control.pending(owner) }.getOrDefault(state.pending),
                resourcePending = runCatching { resources.pending(owner).isNotEmpty() }.getOrDefault(state.resourcePending),
                pendingInstallation = runCatching { installations.pending(owner).any { it.service == InstallationService.Docker } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (state.owner !== owner || session.state.value !== owner) throw CancellationException("Docker control session changed")
    }
}
