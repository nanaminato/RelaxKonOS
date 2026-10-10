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
    val pendingInstallation: Boolean = false, val receipt: SmbReceipt? = null, val problem: String? = null, val checkedAtMillis: Long? = null, val saved: Int = 0)
internal class SmbViewModel(private val session: AuthSession, private val smb: SmbRepository,
    private val installations: InstallationRepository, private val operationIndex: OperationIndex,
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
    var state by mutableStateOf(SmbState()); private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; intent = null; state = SmbState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false, facts = null, installationVerified = false) }
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
    fun change(expected: SmbFacts, change: SmbChange, password: CharArray? = null) {
        if (state.busy || state.owner == null || ServerCapabilities.FILE_SERVICES !in state.owner!!.capabilities) { password?.fill('\u0000'); return }
        work { owner ->
            try {
                val result = smb.change(owner, expected, change, password, elevationAnswers); verify(owner)
                val problem = resultProblem(result)
                val success = result is ApiResult.Success && result.value.succeeded
                load(owner)
                state = state.copy(receipt = (result as? ApiResult.Success)?.value, problem = problem ?: state.problem, saved = state.saved + if (success) 1 else 0)
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
    fun cancelInstall() = work { owner -> state.installation?.operationId?.let { installResult(owner, installations.cancel(owner, it)) } }
    fun recoverInstall(id: String, identified: Boolean) = work { owner ->
        val pending = installations.pending(owner).firstOrNull { it.service == InstallationService.Smb }
        if (pending != null && !identified) return@work
        val result = if (pending == null) installations.recoverById(owner, id.trim()) else installations.identifyOriginal(owner, pending, id.trim())
        installResult(owner, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Smb && result.value.kind == InstallationKind.Install) intent = null
    }
    private suspend fun installResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(owner)
        state = state.copy(pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Smb })
        if (result is ApiResult.Success && result.value.service == InstallationService.Smb && result.value.kind == InstallationKind.Install) {
            state = state.copy(installation = result.value, installationVerified = true, problem = result.value.problemCode)
            if (!result.value.state.active) load(owner)
        } else state = state.copy(installationVerified = false, problem = resultProblem(result) ?: "file-services.smb.unverified")
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = smb.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private fun showFacts(owner: SessionState.Active, result: ApiResult<SmbFacts>) {
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = resultProblem(result) ?: (result as? ApiResult.Success)?.value?.status?.healthProblemCode,
            checkedAtMillis = System.currentTimeMillis(), pending = smb.pending(owner), pendingInstallation = installations.pending(owner).any { it.service == InstallationService.Smb })
    }
    private fun resultProblem(result: ApiResult<*>): String? = when (result) {
        is ApiResult.Problem -> result.code; is ApiResult.Transport -> "file-services.smb.unverified"
        is ApiResult.Success -> (result.value as? SmbReceipt)?.problemCode
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.FILE_SERVICES !in owner.capabilities) return
        val request = generation; state = state.copy(busy = true, problem = null)
        job = viewModelScope.launch {
            try { verify(owner); block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, installationVerified = false, problem = "file-services.smb.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { smb.pending(owner) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { installations.pending(owner).any { it.service == InstallationService.Smb } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) { currentCoroutineContext().ensureActive(); if (state.owner !== owner || session.state.value !== owner) throw CancellationException("SMB session changed") }
}
