package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.auth.AuthSession
import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job

internal data class CertificatesState(val busy: Boolean = false, val list: ApiResult<List<ManagedCertificate>>? = null,
    val deployments: Map<String, ApiResult<KestrelCertificateDeployment>> = emptyMap(),
    val selectedId: String? = null, val detail: ApiResult<ManagedCertificate>? = null,
    val draft: CertificateDraft? = null, val initialDraft: CertificateDraft? = null, val draftLocked: Boolean = false, val preflight: ApiResult<CertificatePreflight>? = null,
    val preflightAtMillis: Long? = null, val operation: CertificateOperation? = null, val operationVerified: Boolean = false,
    val recoveryOperationId: String? = null,
    val pending: List<PendingCertificateRequest> = emptyList(), val problemCode: String? = null, val uncertain: Boolean = false,
    val feedbackVersion: Long = 0)
internal class CertificatesViewModel(private val session: AuthSession, private val repository: CertificateRepository,
    private val operationIndex: OperationIndex, private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    suspend fun observeOperation() {
        val originalOwner = owner ?: return
        val id = state.operation?.operationId ?: return
        fun current() = owner === originalOwner && activeSession === originalOwner && state.operation?.operationId == id && canObserveOperation()
        while (current()) {
            kotlinx.coroutines.delay(1500)
            if (!current()) return
            if (!state.busy) poll()?.join()
        }
    }
    private fun canObserveOperation() = state.operationVerified && state.operation?.state?.active == true &&
        !state.uncertain && state.problemCode == null && state.draft == null

    private val activeSession get() = session.state.value as? SessionState.Active
    private var owner: SessionState.Active? = null
    private var feedbackSequence = 0L
    var sessionEpoch by mutableIntStateOf(0)
        private set
    var state by mutableStateOf(CertificatesState())
        private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== owner) { owner = next as? SessionState.Active; state = CertificatesState(); sessionEpoch++ }
    } } }
    fun refresh() = work { active ->
        state = state.copy(list = null, detail = null, deployments = emptyMap())
        val list = repository.list(active); verify(active)
        state = state.copy(list = list, pending = repository.pending(active))
        failure(list)
        state.selectedId?.let { detail(active, it) }
        if (state.operation == null) (state.recoveryOperationId ?: operationIndex.forOwner(active)
            .firstOrNull { it.domain == OperationDomain.Certificate }?.operationId)?.let { id ->
            state = state.copy(recoveryOperationId = id, operationVerified = false)
            val result = repository.operation(active, id); verify(active)
            if (result is ApiResult.Success) state = state.copy(operation = result.value, operationVerified = true, recoveryOperationId = null)
            else failure(result)
        }
    }
    fun select(id: String?) {
        if (state.busy) return
        state = state.copy(selectedId = id, detail = null)
        if (id != null) work { active -> detail(active, id) }
    }
    private suspend fun detail(active: SessionState.Active, id: String) {
        state = state.copy(detail = null, deployments = state.deployments - id)
        val result = repository.certificate(active, id); verify(active)
        state = state.copy(detail = result); if (result !is ApiResult.Success) failure(result)
        deployment(active, id)
    }
    fun inspectDeployment(id: String) = work { active -> deployment(active, id) }
    private suspend fun deployment(active: SessionState.Active, id: String) {
        state = state.copy(deployments = state.deployments - id)
        val result = repository.kestrel(active, id); verify(active)
        state = state.copy(deployments = state.deployments + (id to result))
    }
    fun create(selfSigned: Boolean) {
        if (state.busy) return
        val draft = CertificateDraft(selfSigned)
        state = state.copy(draft = draft, initialDraft = draft, draftLocked = false, preflight = null, preflightAtMillis = null, problemCode = null, uncertain = false)
    }
    fun update(draft: CertificateDraft) {
        if (!state.busy && !state.draftLocked) state = state.copy(draft = draft, preflight = null, preflightAtMillis = null)
    }
    fun closeDraft() { if (!state.busy) state = state.copy(draft = null, initialDraft = null, draftLocked = false, preflight = null, preflightAtMillis = null) }
    fun preflight() = work { active ->
        val draft = state.draft ?: return@work
        val domains = draft.domains() ?: return@work
        state = state.copy(preflight = null, preflightAtMillis = null)
        val result = repository.preflight(active, domains, draft.challenge); verify(active)
        state = state.copy(preflight = result, preflightAtMillis = System.currentTimeMillis()); failure(result)
    }
    fun submitDraft(expectedOwner: SessionState.Active, expectedDraft: CertificateDraft) {
        if (owner !== expectedOwner || activeSession !== expectedOwner || state.draft != expectedDraft) return
        work { active ->
            val draft = state.draft ?: return@work
            val body = draft.body() ?: return@work
            if (!draft.selfSigned && (state.preflight as? ApiResult.Success)?.value?.canProceed != true) return@work
            state = state.copy(draftLocked = true)
            writing = true
            val result = repository.submit(active, elevationAnswers, draft.action, null, body)
            writing = false
            if (result is ApiResult.Success) { verify(active); closeAfterSubmit() }
            observe(active, result, writeResult = true)
            state = state.copy(draftLocked = state.pending.any { it.target == null && it.attempted } && (result as? ApiResult.Problem)?.code != "certificate.original_request_pending")
        }
    }
    private fun closeAfterSubmit() { state = state.copy(draft = null, initialDraft = null, draftLocked = false, preflight = null, preflightAtMillis = null) }
    fun action(expectedOwner: SessionState.Active, certificate: ManagedCertificate, action: CertificateAction) {
        if (owner !== expectedOwner || activeSession !== expectedOwner || state.selectedId != certificate.id ||
            (state.detail as? ApiResult.Success)?.value != certificate) return
        work { active ->
            if (action == CertificateAction.DeployKestrel) {
                val current = repository.certificate(active, certificate.id); verify(active)
                deployment(active, certificate.id)
                val facts = (state.deployments[certificate.id] as? ApiResult.Success)?.value
                if (current !is ApiResult.Success || current.value.usageProblem() != null || facts?.httpsConfigured != true) {
                    failure(current); state = state.copy(problemCode = "certificate.deployment_not_ready"); return@work
                }
            }
            writing = true
            val result = repository.submit(active, elevationAnswers, action, certificate.id, JsonBody().apply {
                if (action in setOf(CertificateAction.Delete, CertificateAction.Revoke)) bool("confirmed", true)
            })
            writing = false
            observe(active, result, writeResult = true)
        }
    }
    private suspend fun WorkStage.observe(active: SessionState.Active, result: ApiResult<CertificateOperation>, writeResult: Boolean = false) {
        verify(active); failure(result, writeResult)
        if (result is ApiResult.Problem && result.status == 404) state = state.copy(problemCode = "certificate.operation_not_found")
        state = state.copy(operationVerified = result is ApiResult.Success, pending = repository.pending(active))
        if (result is ApiResult.Success) {
            state = state.copy(operation = result.value, operationVerified = true, recoveryOperationId = null)
            receivedOperation = true
            if (result.value.kind == CertificateAction.DeployKestrel) result.value.certificateId?.let { deployment(active, it) }
            if (!result.value.state.active) {
                state = state.copy(list = null, detail = null)
                val list = repository.list(active); verify(active); state = state.copy(list = list); failure(list)
                state.selectedId?.let { detail(active, it) }
            }
        }
    }
    fun recover(id: String, pending: PendingCertificateRequest? = null) = work { active ->
        val closesDraft = pending != null && pending.target == null && state.draftLocked
        val result = repository.recover(active, id.trim(), pending)
        if (result is ApiResult.Success && closesDraft) { verify(active); closeAfterSubmit() }
        observe(active, result)
    }
    fun retry(expectedOwner: SessionState.Active, pending: PendingCertificateRequest) {
        if (owner !== expectedOwner || activeSession !== expectedOwner || pending !in state.pending ||
            pending !in repository.pending(expectedOwner)) return
        if (pending.operationId != null) { recover(pending.operationId, pending); return }
        val id = pending.target ?: return
        work { active ->
            writing = true
            val result = repository.submit(active, elevationAnswers, pending.action, id, JsonBody().apply {
            if (pending.action in setOf(CertificateAction.Delete, CertificateAction.Revoke)) bool("confirmed", true)
            })
            writing = false
            observe(active, result, writeResult = true)
        }
    }
    fun poll() = work { active ->
        val current = state.operation ?: return@work
        val result = repository.operation(active, current.operationId); observe(active, result)
        if (result !is ApiResult.Success && current.kind == CertificateAction.DeployKestrel) current.certificateId?.let { deployment(active, it) }
    }
    fun cancel(expectedOwner: SessionState.Active, operationId: String) {
        if (owner !== expectedOwner || activeSession !== expectedOwner || !state.operationVerified || state.uncertain ||
            state.operation?.operationId != operationId || state.operation?.state?.active != true) return
        work { active ->
            writing = true
            val result = repository.cancel(active, operationId)
            writing = false
            observe(active, result, writeResult = true)
        }
    }
    private fun failure(result: ApiResult<*>, writeResult: Boolean = false) {
        val feedback = certificateFailure(result, writeResult)
        state = state.copy(problemCode = feedback.problemCode, uncertain = feedback.uncertain)
    }
    private class WorkStage { var writing = false; var receivedOperation = false }
    private fun work(block: suspend WorkStage.(SessionState.Active) -> Unit): Job? {
        val active = activeSession ?: return null
        if (owner !== active) { owner = active; state = CertificatesState(); sessionEpoch++ }
        if (state.busy) return null
        state = state.copy(busy = true, problemCode = null, uncertain = false, feedbackVersion = ++feedbackSequence)
        return viewModelScope.launch {
            val stage = WorkStage()
            try { stage.block(active) }
            catch (cancelled: CancellationException) {
                if (activeSession === active) {
                    failure(ApiResult.Transport(null), stage.writing || state.uncertain)
                    state = state.copy(operationVerified = stage.receivedOperation && state.operationVerified,
                        pending = runCatching { repository.pending(active) }.getOrDefault(state.pending))
                }
                throw cancelled
            }
            catch (_: Exception) { if (activeSession === active) {
                failure(ApiResult.Transport(null), stage.writing || state.uncertain)
                state = state.copy(operationVerified = stage.receivedOperation && state.operationVerified,
                    pending = runCatching { repository.pending(active) }.getOrDefault(state.pending))
            } }
            finally { if (activeSession === active) state = state.copy(busy = false) }
        }
    }
    private fun verify(active: SessionState.Active) { if (activeSession !== active) throw CancellationException("Certificate session changed") }
}
