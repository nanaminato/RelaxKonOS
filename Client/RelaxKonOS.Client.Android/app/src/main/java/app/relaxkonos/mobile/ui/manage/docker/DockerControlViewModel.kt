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
    val savedChange: DockerControlChange? = null, val feedbackVersion: Long = 0, val installationLookupId: String? = null, val installationLookupMissing: Boolean = false, val installationLookupFailed: Boolean = false)
/** A missing historical receipt must not invalidate the independently verified engine facts. */
internal fun DockerControlState.installationLookupFailure(id: String?, result: ApiResult<*>): DockerControlState = copy(
    installationVerified = false,
    installationLookupId = id,
    installationLookupMissing = result is ApiResult.Problem && result.status == 404,
    installationLookupFailed = true,
)

internal class DockerInstallationCancellation(val owner: SessionState.Active, val operation: InstallationOperation)
internal class ControlConfirmation(val owner: SessionState.Active, val facts: DockerControlFacts, val change: DockerControlChange)
internal data class DockerRecoveryDraft(val id: String, val identified: Boolean = false)
internal class DockerRecoveryForm(val owner: SessionState.Active, val generation: Int,
    val pending: PendingInstallationRequest?, initialId: String) {
    var draft by mutableStateOf(DockerRecoveryDraft(initialId)); internal set
}
internal class DockerControlViewModel(private val session: AuthSession, private val control: DockerControlRepository,
    private val resources: DockerResourceRepository, private val installations: InstallationRepository,
    private val operationIndex: OperationIndex, private val mutationGate: DockerMutationGate,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    var installationCancellation by mutableStateOf<DockerInstallationCancellation?>(null); private set
    fun dismissInstallationCancellation(review: DockerInstallationCancellation) {
        if (installationCancellation === review) installationCancellation = null
    }
    fun requestInstallationCancellation(owner: SessionState.Active, operation: InstallationOperation) {
        if (!state.busy && recoveryForm == null && canCancelInstallation(owner, operation)) installationCancellation = DockerInstallationCancellation(owner, operation)
    }
    var confirmation by mutableStateOf<ControlConfirmation?>(null); private set
    fun dismissConfirmation(review: ControlConfirmation) {
        if (confirmation === review) confirmation = null
    }
    fun withdrawReviews(owner: SessionState.Active?) {
        if (state.owner === owner) { confirmation = null; installationCancellation = null; recoveryForm = null }
    }
    var recoveryForm by mutableStateOf<DockerRecoveryForm?>(null); private set
    private var lastRecoveryId = ""
    fun requestRecovery(owner: SessionState.Active): DockerRecoveryForm? {
        if (state.owner !== owner || session.state.value !== owner || !owner.privilegedOperations ||
            ServerCapabilities.DOCKER !in owner.capabilities || state.busy || recoveryForm != null ||
            confirmation != null || installationCancellation != null) return null
        val pending = try { installations.pending(owner).firstOrNull { it.service == InstallationService.Docker } }
        catch (_: CancellationException) { return null }
        catch (_: Exception) { state = state.copy(pendingInstallation = true, problem = "docker.control.recovery_storage_failed"); return null }
        if (state.pendingInstallation != (pending != null)) {
            state = state.copy(pendingInstallation = pending != null, problem = "docker.control.unverified"); return null
        }
        return DockerRecoveryForm(owner, generation, pending, pending?.operationId ?: lastRecoveryId.ifEmpty {
            state.installation?.operationId.takeIf { state.installationVerified }.orEmpty()
        }).also { recoveryForm = it }
    }
    private fun ownsRecovery(form: DockerRecoveryForm) = recoveryForm === form && !state.busy &&
        current(form.owner, form.generation)
    fun changeRecoveryId(form: DockerRecoveryForm, id: String) {
        if (ownsRecovery(form) && form.draft.id != id) { form.draft = DockerRecoveryDraft(id); lastRecoveryId = id }
    }
    fun identifyRecovery(form: DockerRecoveryForm, draft: DockerRecoveryDraft, identified: Boolean) {
        if (ownsRecovery(form) && form.draft === draft && form.pending != null) form.draft = draft.copy(identified = identified)
    }
    fun dismissRecovery(form: DockerRecoveryForm) { if (ownsRecovery(form)) recoveryForm = null }
    fun submitRecovery(form: DockerRecoveryForm, draft: DockerRecoveryDraft) {
        if (!ownsRecovery(form) || form.draft !== draft || form.pending != null && !draft.identified) return
        val id = runCatching { InstallationRoutes.canonicalId(draft.id.trim()) }.getOrNull() ?: return
        recoveryForm = null
        work { owner ->
            if (owner !== form.owner) return@work
            val pending = installations.pending(owner).firstOrNull { it.service == InstallationService.Docker }
            if (pending != form.pending) {
                state = state.copy(pendingInstallation = pending != null, problem = "docker.control.unverified"); return@work
            }
            val result = if (pending == null) installations.recoverById(owner, id, InstallationService.Docker, setOf(InstallationKind.Install))
                else installations.identifyOriginal(owner, pending, id, setOf(InstallationKind.Install))
            installResult(owner, result)
            if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) intent = null
        }
    }
    fun requestChange(owner: SessionState.Active, facts: DockerControlFacts, change: DockerControlChange) {
        if (session.state.value !== owner || state.owner !== owner || !owner.privilegedOperations ||
            state.busy || recoveryForm != null || state.facts != facts || state.pending.isNotEmpty() || state.resourcePending ||
            state.pendingInstallation || state.installation?.state?.active == true ||
            state.installation != null && !state.installationVerified) return
        confirmation = ControlConfirmation(owner, facts, change)
    }
    suspend fun observeInstallation(canObserve: () -> Boolean) {
        val owner = state.owner ?: return
        val id = state.installation?.operationId ?: return
        var observedGeneration = generation
        fun eligible() = canObserve() && recoveryForm == null && installationCancellation == null && confirmation == null && state.owner === owner && session.state.value === owner && state.problem == null &&
            state.installationVerified && state.installation?.let { it.operationId == id && it.state.active } == true
        while (eligible()) {
            if (state.busy) {
                val pending = job ?: return
                pending.join()
                observedGeneration = generation
                continue
            }
            delay(1500)
            if (!eligible()) return
            if (state.busy || generation != observedGeneration) {
                observedGeneration = generation
                continue
            }
            pollInstall()
            if (generation == observedGeneration) return
            val pending = job ?: return
            pending.join()
            observedGeneration = generation
        }
    }


    private var job: Job? = null
    private var generation = 0
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(DockerControlState()); private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { confirmation = null; installationCancellation = null; recoveryForm = null; lastRecoveryId = ""; job?.cancel(); generation++; intent = null; state = DockerControlState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { confirmation = null; installationCancellation = null; recoveryForm = null; lastRecoveryId = ""; job?.cancel(); generation++; state = state.copy(busy = false, facts = null, installationVerified = false) }
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
    fun change(review: ControlConfirmation) {
        if (confirmation !== review) return
        confirmation = null
        if (session.state.value !== review.owner || state.owner !== review.owner || state.facts != review.facts) return
        val expected = review.facts; val change = review.change
        work { owner ->
            require(!state.pendingInstallation && state.installation?.state?.active != true && (state.installation == null || state.installationVerified))
            val outcome = control.change(owner, expected, change); verify(owner)
            val result = outcome.result
            if (result is ApiResult.Success) state = state.copy(saved = state.saved + 1, savedChange = change)
            showFacts(owner, outcome.facts)
            state = state.copy(problem = when {
                outcome.storageFailed -> "docker.control.recovery_storage_failed"
                problem(result) != null -> problem(result)
                result is ApiResult.Success && outcome.facts !is ApiResult.Success -> "docker.control.refresh_failed"
                else -> state.problem
            })
        }
    }
    fun accept(pending: PendingDockerControl) = work { owner ->
        val result = control.acceptFacts(owner, pending); verify(owner); showFacts(owner, result)
    }
    fun pollInstall() = work { owner -> state.installation?.operationId?.let { installResult(owner, installations.operation(owner, it)) } }
    private fun canCancelInstallation(expectedOwner: SessionState.Active, operation: InstallationOperation): Boolean =
        state.owner === expectedOwner && session.state.value === expectedOwner && expectedOwner.privilegedOperations &&
            state.installationVerified && state.installation == operation && operation.state.active && operation.cancellable
    fun cancelInstall(review: DockerInstallationCancellation) {
        if (installationCancellation !== review) return
        installationCancellation = null
        if (state.busy || !canCancelInstallation(review.owner, review.operation)) return
        work { owner ->
            if (!canCancelInstallation(review.owner, review.operation)) return@work
            installResult(owner, installations.cancel(owner, review.operation.operationId))
        }
    }
    private suspend fun installResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>, observing: Boolean = false) {
        verify(owner)
        if (result is ApiResult.Success && result.value.service == InstallationService.Docker && result.value.kind == InstallationKind.Install) {
            val operationProblem = result.value.problemCode?.takeIf(String::isNotBlank)
            state = state.copy(installation = result.value, installationVerified = true,
                installationLookupId = result.value.operationId, installationLookupMissing = false, installationLookupFailed = false,
                problem = if (observing) state.problem ?: operationProblem else operationProblem)
            try {
                state = state.copy(pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Docker })
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                state = state.copy(facts = null, pendingInstallation = true, problem = "docker.control.recovery_storage_failed")
                return
            }
            if (!observing && !result.value.state.active) {
                val facts = try { control.facts(owner) }
                catch (cancelled: CancellationException) {
                    currentCoroutineContext().ensureActive()
                    verify(owner)
                    ApiResult.Transport(null)
                } catch (_: Exception) { verify(owner); ApiResult.Transport(null) }
                verify(owner)
                showFacts(owner, facts)
                state = state.copy(problem = when {
                    state.problem == "docker.control.recovery_storage_failed" -> state.problem
                    facts !is ApiResult.Success -> "docker.control.installation_refresh_failed"
                    else -> operationProblem ?: state.problem
                })
            }
        } else state = state.copy(installationVerified = false, problem = problem(result) ?: "docker.control.unverified")
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = control.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private fun showFacts(owner: SessionState.Active, result: ApiResult<DockerControlFacts>) {
        val resourcePending: Boolean
        val pending: List<PendingDockerControl>
        val pendingInstallation: Boolean
        try {
            resourcePending = resources.pending(owner).isNotEmpty()
            pending = control.pending(owner)
            pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Docker }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) {
            state = state.copy(facts = null, checkedAtMillis = null, problem = "docker.control.recovery_storage_failed")
            return
        }
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = problem(result), checkedAtMillis = System.currentTimeMillis(),
            resourcePending = resourcePending, pending = pending, pendingInstallation = pendingInstallation)
    }
    private fun problem(result: ApiResult<*>): String? = when (result) {
        is ApiResult.Problem -> result.code; is ApiResult.Transport -> "docker.control.unverified"; is ApiResult.Success -> null
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        recoveryForm = null
        confirmation = null
        installationCancellation = null
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.DOCKER !in owner.capabilities) return
        val request = ++generation; state = state.copy(busy = true, problem = null, feedbackVersion = state.feedbackVersion + 1)
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
