package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class SmbState(val owner: SessionState.Active? = null, val busy: Boolean = false, val facts: SmbFacts? = null,
    val pending: List<PendingSmbMutation> = emptyList(), val installation: InstallationOperation? = null, val installationVerified: Boolean = false,
    val pendingInstallation: Boolean = false, val receipt: SmbReceipt? = null, val problem: String? = null, val checkedAtMillis: Long? = null, val saved: Int = 0, val feedbackVersion: Long = 0)
internal class SmbChangeReview(val owner: SessionState.Active, val confirmation: SmbConfirmation)
internal class SmbInstallationCancellation(val owner: SessionState.Active, val operation: InstallationOperation)
internal class SmbViewModel(private val session: AuthSession, private val smb: SmbRepository,
    private val installations: InstallationRepository, private val operationIndex: OperationIndex,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    var changeReview by mutableStateOf<SmbChangeReview?>(null); private set
    var installationCancellation by mutableStateOf<SmbInstallationCancellation?>(null); private set
    fun dismissInstallationCancellation() { installationCancellation = null }
    fun requestInstallationCancellation(owner: SessionState.Active, operation: InstallationOperation) {
        if (session.state.value === owner && state.owner === owner && owner.privilegedOperations && !state.busy &&
            state.installationVerified && state.installation == operation && operation.state.active && operation.cancellable)
            installationCancellation = SmbInstallationCancellation(owner, operation)
    }
    fun dismissConfirmation() { changeReview = null }
    fun requestChange(owner: SessionState.Active, expected: SmbFacts, change: SmbChange) {
        if (session.state.value !== owner || state.owner !== owner || !owner.privilegedOperations || state.busy ||
            state.facts != expected || state.pending.isNotEmpty() || state.pendingInstallation ||
            state.installation?.state?.active == true || state.installation != null && !state.installationVerified ||
            !expected.capabilities.supported || !expected.status.state.manageable) return
        changeReview = SmbChangeReview(owner, SmbConfirmation(expected, change))
    }
    suspend fun observeInstallation(canObserve: () -> Boolean) {
        val owner = state.owner ?: return
        val id = state.installation?.operationId ?: return
        val request = generation
        fun eligible() = canObserve() && installationCancellation == null && session.state.value === owner && state.owner === owner &&
            state.installationVerified && state.installation?.let { it.operationId == id && it.state.active } == true && !state.busy
        if (eligible()) {
            kotlinx.coroutines.delay(1500)
            if (eligible() && generation == request) pollInstall()
        }
    }

    private var job: Job? = null
    private var generation = 0
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(SmbState()); private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { dismissConfirmation(); dismissInstallationCancellation(); job?.cancel(); generation++; intent = null; state = SmbState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { dismissConfirmation(); dismissInstallationCancellation(); job?.cancel(); generation++; state = state.copy(busy = false, facts = null, installationVerified = false) }
    fun refresh() = work { owner ->
        load(owner)
        if (owner.privilegedOperations) {
            val known = installations.pending(owner).firstOrNull { it.service == InstallationService.Smb && it.operationId != null }
            if (known != null) {
                val result = installations.recover(owner, known); verify(owner)
                if (result is ApiResult.Success && result.value != null) installResult(owner, ApiResult.Success(result.value))
                else state = state.copy(installationVerified = false)
                return@work
            }
            val active = installations.active(owner, InstallationService.Smb); verify(owner)
            if (active is ApiResult.Success && active.value != null) installResult(owner, ApiResult.Success(active.value))
            else if (active is ApiResult.Success) {
                val id = state.installation?.operationId ?: operationIndex.forOwner(owner).firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Smb.name }?.operationId
                if (id != null) installResult(owner, installations.operation(owner, id))
            } else state = state.copy(installationVerified = false)
        }
    }
    fun accept(pending: PendingSmbMutation) = work { owner ->
        val result = smb.acceptFacts(owner, pending); verify(owner)
        showFacts(owner, result)
    }
    fun change(review: SmbChangeReview, password: CharArray? = null) {
        if (changeReview !== review) { password?.fill('\u0000'); return }
        dismissConfirmation()
        if (state.busy || session.state.value !== review.owner || state.owner !== review.owner ||
            state.facts != review.confirmation.expected || ServerCapabilities.FILE_SERVICES !in review.owner.capabilities) { password?.fill('\u0000'); return }
        val expected = review.confirmation.expected; val change = review.confirmation.change
        state = state.copy(receipt = null)
        work { owner ->
            try {
                val outcome = smb.change(owner, expected, change, password, elevationAnswers); verify(owner)
                val result = outcome.result
                val problem = resultProblem(result)
                val success = result is ApiResult.Success && result.value.succeeded
                // A known receipt closes the submitted draft before local recovery reads can fail.
                state = state.copy(receipt = (result as? ApiResult.Success)?.value, saved = state.saved + if (success) 1 else 0)
                showFacts(owner, outcome.facts)
                val verificationFailed = outcome.facts !is ApiResult.Success || !outcome.facts.value.capabilities.supported ||
                    !outcome.facts.value.status.state.manageable
                state = state.copy(problem = when {
                    outcome.storageFailed -> "file-services.smb.recovery_storage_failed"
                    problem != null -> problem
                    success && verificationFailed -> "file-services.smb.refresh_failed"
                    else -> state.problem
                })
            } finally { password?.fill('\u0000') }
        }
        job?.invokeOnCompletion { password?.fill('\u0000') }
    }
    fun install() = work { owner ->
        require(state.facts?.capabilities?.installSupported == true && owner.privilegedOperations && state.pending.isEmpty())
        if (intent == null) intent = installations.prepare(owner, InstallationKind.Install, SmbInstallationRequest(true))
        val result = installations.submit(requireNotNull(intent), elevationAnswers); verify(owner)
        installResult(owner, result)
        if (result is ApiResult.Success || result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun pollInstall() = work { owner -> state.installation?.operationId?.let { installResult(owner, installations.operation(owner, it)) } }
    fun cancelInstall(review: SmbInstallationCancellation) {
        if (installationCancellation !== review) return
        dismissInstallationCancellation()
        if (session.state.value !== review.owner || state.owner !== review.owner || !state.installationVerified ||
            state.installation != review.operation || !review.operation.state.active || !review.operation.cancellable) return
        work { owner -> installResult(owner, installations.cancel(owner, review.operation.operationId)) }
    }
    fun recoverInstall(id: String, identified: Boolean) = work { owner ->
        val pending = installations.pending(owner).firstOrNull { it.service == InstallationService.Smb }
        if (pending != null && !identified) return@work
        val result = if (pending == null) installations.recoverById(owner, id.trim()) else installations.identifyOriginal(owner, pending, id.trim())
        installResult(owner, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Smb && result.value.kind == InstallationKind.Install) intent = null
    }
    private suspend fun installResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(owner)
        if (result is ApiResult.Success && result.value.service == InstallationService.Smb && result.value.kind == InstallationKind.Install) {
            val operationProblem = result.value.problemCode?.takeIf(String::isNotBlank)
            state = state.copy(installation = result.value, installationVerified = true, problem = operationProblem)
            try {
                state = state.copy(pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Smb })
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                state = state.copy(facts = null, pendingInstallation = true, problem = "file-services.smb.recovery_storage_failed")
                return
            }
            if (!result.value.state.active) {
                val facts = try { smb.facts(owner) }
                catch (cancelled: CancellationException) {
                    currentCoroutineContext().ensureActive()
                    verify(owner)
                    ApiResult.Transport(null)
                } catch (_: Exception) { verify(owner); ApiResult.Transport(null) }
                verify(owner)
                showFacts(owner, facts)
                state = state.copy(problem = when {
                    state.problem == "file-services.smb.recovery_storage_failed" -> state.problem
                    facts !is ApiResult.Success -> "file-services.smb.installation_refresh_failed"
                    else -> operationProblem ?: state.problem
                })
            }
        } else state = state.copy(installationVerified = false, problem = resultProblem(result) ?: "file-services.smb.unverified")
    }

    private suspend fun load(owner: SessionState.Active) {
        val result = smb.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private fun showFacts(owner: SessionState.Active, result: ApiResult<SmbFacts>) {
        val pending: List<PendingSmbMutation>
        val pendingInstallation: Boolean
        try {
            pending = smb.pending(owner)
            pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Smb }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) {
            state = state.copy(facts = null, checkedAtMillis = null, problem = "file-services.smb.recovery_storage_failed")
            return
        }
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = resultProblem(result) ?: (result as? ApiResult.Success)?.value?.status?.healthProblemCode,
            checkedAtMillis = System.currentTimeMillis(), pending = pending, pendingInstallation = pendingInstallation)
    }
    private fun resultProblem(result: ApiResult<*>): String? = when (result) {
        is ApiResult.Problem -> result.code; is ApiResult.Transport -> "file-services.smb.unverified"
        is ApiResult.Success -> (result.value as? SmbReceipt)?.problemCode?.takeIf(String::isNotBlank)
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        dismissConfirmation()
        dismissInstallationCancellation()
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.FILE_SERVICES !in owner.capabilities) return
        val request = ++generation; state = state.copy(busy = true, problem = null, feedbackVersion = state.feedbackVersion + 1)
        job = viewModelScope.launch {
            try { verify(owner); block(owner) }
            catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(facts = null, installationVerified = false,
                    problem = "file-services.smb.unverified")
                throw cancelled
            }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, installationVerified = false, problem = "file-services.smb.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { smb.pending(owner) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { installations.pending(owner).any { it.service == InstallationService.Smb } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) { currentCoroutineContext().ensureActive(); if (state.owner !== owner || session.state.value !== owner) throw CancellationException("SMB session changed") }
}
