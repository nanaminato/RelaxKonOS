package app.relaxkonos.mobile.ui.manage.tunnels

import android.app.Application
import android.net.Uri
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class TunnelsState(val busy: Boolean = false, val facts: ApiResult<TunnelFacts>? = null, val runtime: ApiResult<TunnelRuntime>? = null,
    val selectedId: String? = null, val profileDraft: TunnelProfileDraft? = null, val initialProfile: TunnelProfileDraft? = null,
    val definitionDraft: TunnelDefinitionDraft? = null, val initialDefinition: TunnelDefinitionDraft? = null,
    val detected: ApiResult<TunnelRuntime>? = null, val download: ApiResult<TunnelRuntimeDownload>? = null,
    val logs: ApiResult<List<TunnelLog>>? = null, val logsAtMillis: Long? = null, val action: ApiResult<TunnelResult>? = null,
    val pending: List<PendingTunnelMutation> = emptyList(), val installation: InstallationOperation? = null, val installationVerified: Boolean = false,
    val pendingInstallation: Boolean = false, val reference: InstallationFileReference? = null, val uploadBytes: Long? = null,
    val frps: ApiResult<ManagedFrps>? = null, val frpsAtMillis: Long? = null, val frpsDraft: ManagedFrpsDraft? = null,
    val initialFrps: ManagedFrpsDraft? = null, val editingToken: CharArray? = null, val frpsAction: ApiResult<TunnelResult>? = null,
    val frpsLogs: ApiResult<List<TunnelLog>>? = null, val frpsAudit: ApiResult<List<TunnelAudit>>? = null, val frpsDiagnosticsAtMillis: Long? = null,
    val problemCode: String? = null, val uncertain: Boolean = false)
internal class TunnelsViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var owner: SessionState.Active? = null
    private var intent: InstallationSubmission? = null
    private var frpsSecretEpoch = 0
    var state by mutableStateOf(TunnelsState())
        private set
    var sessionEpoch by mutableIntStateOf(0)
        private set
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== owner) { state.editingToken?.fill('\u0000'); owner = next as? SessionState.Active; intent = null; state = TunnelsState(); sessionEpoch++ }
    } } }
    fun refresh() = work { active -> load(active)
        if (active.privilegedOperations) {
            val result = container.installations.active(active, InstallationService.Frp); verify(active)
            if (result is ApiResult.Success && result.value != null) state = state.copy(installation = result.value, installationVerified = true)
            else if (result !is ApiResult.Success) state = state.copy(installationVerified = false)
            else if (state.installation == null) container.operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Frp.name }?.let {
                installationResult(active, container.installations.operation(active, it.operationId))
            }
        }
    }
    fun observe() = work { active -> load(active) }
    private suspend fun load(active: SessionState.Active) {
        val facts = container.tunnels.facts(active); val runtime = container.tunnels.runtime(active); val frps = container.tunnels.frps(active); verify(active)
        state = state.copy(facts = facts, runtime = runtime, frps = frps, frpsAtMillis = System.currentTimeMillis(), pending = container.tunnels.pending(active),
            pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Frp },
            problemCode = listOf(facts, runtime).filterIsInstance<ApiResult.Problem>().firstOrNull()?.code,
            uncertain = facts !is ApiResult.Success || runtime !is ApiResult.Success)
    }
    fun select(id: String?) { if (!state.busy) state = state.copy(selectedId = id, logs = null, logsAtMillis = null, action = null) }
    fun editProfile(profile: TunnelProfile? = null) { if (!state.busy && state.pending.isEmpty()) {
        val draft = profile?.let(TunnelProfileDraft::from) ?: TunnelProfileDraft()
        state = state.copy(profileDraft = draft, initialProfile = draft, detected = null)
    } }
    fun updateProfile(draft: TunnelProfileDraft) { if (!state.busy && state.pending.isEmpty()) state = state.copy(profileDraft = draft, detected = null) }
    fun closeProfile() { if (!state.busy) state = state.copy(profileDraft = null, initialProfile = null, detected = null) }
    fun editDefinition(profileId: String, definition: TunnelDefinition? = null) { if (!state.busy && state.pending.isEmpty()) {
        val draft = definition?.let(TunnelDefinitionDraft::from) ?: TunnelDefinitionDraft(profileId = profileId)
        state = state.copy(definitionDraft = draft, initialDefinition = draft)
    } }
    fun updateDefinition(draft: TunnelDefinitionDraft) { if (!state.busy && state.pending.isEmpty()) state = state.copy(definitionDraft = draft) }
    fun closeDefinition() { if (!state.busy) state = state.copy(definitionDraft = null, initialDefinition = null) }
    fun saveProfile() = work { active ->
        val draft = state.profileDraft ?: return@work; val request = draft.request() ?: return@work
        val result = container.tunnels.saveProfile(active, draft.id, request); verify(active); failure(result)
        state = state.copy(pending = container.tunnels.pending(active))
        if (result is ApiResult.Success) { state = state.copy(profileDraft = null, initialProfile = null, selectedId = result.value.id); load(active) }
    }
    fun saveDefinition() = work { active ->
        val draft = state.definitionDraft ?: return@work; val request = draft.request() ?: return@work
        val result = container.tunnels.saveDefinition(active, draft.id, request); verify(active); failure(result)
        state = state.copy(pending = container.tunnels.pending(active))
        if (result is ApiResult.Success) { state = state.copy(definitionDraft = null, initialDefinition = null); load(active) }
    }
    fun reloadDraft() = work { active ->
        load(active)
        if (state.pending.isNotEmpty()) return@work
        val facts = (state.facts as? ApiResult.Success)?.value ?: return@work
        state.profileDraft?.id?.let { id -> facts.profiles.firstOrNull { it.id == id }?.let { current -> val draft = TunnelProfileDraft.from(current); state = state.copy(profileDraft = draft, initialProfile = draft) } }
        state.definitionDraft?.id?.let { id -> facts.definitions.firstOrNull { it.id == id }?.let { current -> val draft = TunnelDefinitionDraft.from(current); state = state.copy(definitionDraft = draft, initialDefinition = draft) } }
    }
    fun deleteProfile(profile: TunnelProfile) = mutation { active -> container.tunnels.deleteProfile(active, profile, container.elevationAnswers) }
    fun deleteDefinition(definition: TunnelDefinition) = mutation { active -> container.tunnels.deleteDefinition(active, definition) }
    fun setToken(profile: TunnelProfile, token: String) = mutation { active -> container.tunnels.setToken(active, profile.id, token) }
    private fun mutation(call: suspend (SessionState.Active) -> ApiResult<Unit>) = work { active ->
        val result = call(active); verify(active); failure(result); state = state.copy(pending = container.tunnels.pending(active))
        if (result is ApiResult.Success) load(active)
    }
    fun lifecycle(profile: TunnelProfile, apply: Boolean) = work { active ->
        val result = container.tunnels.lifecycle(active, profile.id, apply, container.elevationAnswers); verify(active); failure(result)
        load(active); state = state.copy(action = result)
        if (result is ApiResult.Success && !result.value.succeeded) state = state.copy(problemCode = result.value.problemCode)
    }
    fun accept(pending: PendingTunnelMutation) = work { active ->
        if (pending.action.frps) {
            val result = container.tunnels.acceptFrpsFacts(active, pending); verify(active); failure(result)
            state = state.copy(frps = result, frpsAtMillis = System.currentTimeMillis(), pending = container.tunnels.pending(active))
            if (result is ApiResult.Success) { state.editingToken?.fill('\u0000'); state = state.copy(frpsDraft = null, initialFrps = null, editingToken = null) }
        } else {
            val result = container.tunnels.acceptFacts(active, pending); verify(active); failure(result)
            state = state.copy(facts = result, pending = container.tunnels.pending(active))
            if (result is ApiResult.Success) state = state.copy(profileDraft = null, initialProfile = null, definitionDraft = null, initialDefinition = null)
        }
    }
    fun observeFrps() = work { active ->
        val result = container.tunnels.frps(active); verify(active)
        state = state.copy(frps = result, frpsAtMillis = System.currentTimeMillis(), pending = container.tunnels.pending(active)); failure(result)
    }
    fun editFrps() {
        if (state.busy || state.pending.isNotEmpty()) return
        val current = (state.frps as? ApiResult.Success)?.value ?: return
        state.editingToken?.fill('\u0000'); val draft = ManagedFrpsDraft.from(current); state = state.copy(frpsDraft = draft, initialFrps = draft, editingToken = null)
    }
    fun updateFrps(draft: ManagedFrpsDraft) { if (!state.busy && state.pending.isEmpty()) state = state.copy(frpsDraft = draft) }
    fun closeFrps() { if (!state.busy) { state.editingToken?.fill('\u0000'); state = state.copy(frpsDraft = null, initialFrps = null, editingToken = null) } }
    fun reloadFrpsDraft() = work { active ->
        val result = container.tunnels.frps(active); verify(active); failure(result)
        state = state.copy(frps = result, frpsAtMillis = System.currentTimeMillis())
        if (result is ApiResult.Success && state.pending.isEmpty()) {
            state.editingToken?.fill('\u0000'); val draft = ManagedFrpsDraft.from(result.value); state = state.copy(frpsDraft = draft, initialFrps = draft, editingToken = null)
        }
    }
    fun clearFrpsSecret() {
        frpsSecretEpoch++; state.editingToken?.fill('\u0000'); state = state.copy(editingToken = null)
    }
    fun readFrpsToken() {
        val draft = state.frpsDraft ?: return; val epoch = frpsSecretEpoch
        work { active ->
            val result = container.tunnels.frpsEditing(active, draft.revision)
            try { verify(active) } catch (error: Throwable) { (result as? ApiResult.Success)?.value?.token?.fill('\u0000'); throw error }
            if (epoch != frpsSecretEpoch || state.frpsDraft != draft) {
                (result as? ApiResult.Success)?.value?.token?.fill('\u0000'); return@work
            }
            failure(result); state.editingToken?.fill('\u0000')
            state = state.copy(editingToken = (result as? ApiResult.Success)?.value?.token)
        }
    }
    fun saveFrps(token: CharArray, password: CharArray) {
        val submittedOwner = container.activeSession; val draft = state.frpsDraft
        if (submittedOwner == null || state.busy || draft == null) { token.fill('\u0000'); password.fill('\u0000'); return }
        work { active ->
            try {
                if (active !== submittedOwner || state.frpsDraft != draft) return@work
                val request = draft.request(token, password) ?: return@work
                val result = container.tunnels.saveFrps(active, request); verify(active); failure(result)
                state.editingToken?.fill('\u0000')
                state = state.copy(pending = container.tunnels.pending(active), editingToken = null)
                if (result is ApiResult.Success) state = state.copy(frps = result, frpsAtMillis = System.currentTimeMillis(), frpsDraft = null, initialFrps = null)
            } finally { token.fill('\u0000'); password.fill('\u0000') }
        }
    }
    fun lifecycleFrps(action: TunnelMutation) = work { active ->
        val result = container.tunnels.frpsLifecycle(active, action, container.elevationAnswers); verify(active); failure(result)
        val current = container.tunnels.frps(active); verify(active)
        state = state.copy(frpsAction = result, frps = current, frpsAtMillis = System.currentTimeMillis(), pending = container.tunnels.pending(active))
        if (result is ApiResult.Success && !result.value.succeeded) state = state.copy(problemCode = result.value.problemCode)
    }
    fun diagnosticsFrps() = work { active ->
        val logs = container.tunnels.frpsLogs(active); val audit = container.tunnels.frpsAudit(active); verify(active)
        state = state.copy(frpsLogs = logs, frpsAudit = audit, frpsDiagnosticsAtMillis = System.currentTimeMillis())
        failure(if (logs !is ApiResult.Success) logs else audit)
    }
    fun detect(path: String) = work { active -> val result = container.tunnels.detect(active, path); verify(active); state = state.copy(detected = result); failure(result) }
    fun logs(profileId: String) = work { active -> val result = container.tunnels.logs(active, profileId); verify(active); state = state.copy(logs = result, logsAtMillis = System.currentTimeMillis()); failure(result) }
    fun download(version: String) = work { active -> val result = container.tunnels.download(active, version); verify(active); state = state.copy(download = result); failure(result) }
    fun clearReference() { if (!state.busy && intent == null) state = state.copy(reference = null) }
    fun reference(path: String) = work { active -> val result = container.installations.fileReference(active, InstallationService.Frp, path); verify(active); state = state.copy(reference = (result as? ApiResult.Success)?.value); failure(result) }
    fun upload(uri: Uri) = work { active ->
        val document = container.uploadDocuments.open(uri.toString()) ?: run { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
        val result = container.installations.upload(active, InstallationService.Frp, document) { bytes -> viewModelScope.launch { if (container.activeSession === active && state.busy) state = state.copy(uploadBytes = bytes) } }
        verify(active); state = state.copy(reference = (result as? ApiResult.Success)?.value, uploadBytes = null); failure(result)
    }
    fun install(kind: InstallationKind, version: String?, rollback: Boolean, packageSource: Boolean) = work { active ->
        if (intent == null) {
            if (packageSource && state.reference?.expired() != false) { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
            intent = container.installations.prepare(active, kind, FrpInstallationRequest(true, version?.trim()?.takeIf(String::isNotEmpty), rollback, if (packageSource) state.reference?.id else null))
        }
        val result = container.installations.submit(requireNotNull(intent), container.elevationAnswers); verify(active); failure(result)
        state = state.copy(pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Frp })
        if (result is ApiResult.Success) { intent = null; state = state.copy(installation = result.value, installationVerified = true, reference = null, pendingInstallation = false) }
        else if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun retryInstall() { val old = intent ?: return; val r = old.request as FrpInstallationRequest; install(old.kind, r.version, r.rollback, r.fileReferenceId != null) }
    val hasIntent get() = intent != null
    val currentIntent get() = intent
    fun recoverInstall(id: String, identified: Boolean) = work { active ->
        val pending = container.installations.pending(active).firstOrNull { it.service == InstallationService.Frp }
        if (pending != null && !identified) return@work
        val result = if (pending == null) container.installations.recoverById(active, id.trim()) else container.installations.identifyOriginal(active, pending, id.trim())
        installationResult(active, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Frp) intent = null
    }
    fun pollInstall() = work { active -> val id = state.installation?.operationId ?: return@work; installationResult(active, container.installations.operation(active, id)) }
    fun cancelInstall() = work { active -> val id = state.installation?.operationId ?: return@work; installationResult(active, container.installations.cancel(active, id)) }
    private suspend fun installationResult(active: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(active); failure(result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Frp) {
            state = state.copy(installation = result.value, installationVerified = true,
                pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Frp })
            if (!result.value.state.active) load(active)
        } else state = state.copy(installationVerified = false, uncertain = true)
    }
    private fun failure(result: ApiResult<*>) { state = state.copy(problemCode = (result as? ApiResult.Problem)?.code, uncertain = result is ApiResult.Transport) }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = container.activeSession ?: return
        if (owner !== active) { state.editingToken?.fill('\u0000'); owner = active; intent = null; state = TunnelsState(); sessionEpoch++ }
        if (state.busy) return
        state = state.copy(busy = true, problemCode = null, uncertain = false)
        viewModelScope.launch {
            try { block(active) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (container.activeSession === active) state = state.copy(uncertain = true, installationVerified = false, pending = runCatching { container.tunnels.pending(active) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { container.installations.pending(active).any { it.service == InstallationService.Frp } }.getOrDefault(state.pendingInstallation)) }
            finally { if (container.activeSession === active) state = state.copy(busy = false) }
        }
    }
    override fun onCleared() { frpsSecretEpoch++; state.editingToken?.fill('\u0000'); super.onCleared() }
    private fun verify(active: SessionState.Active) { if (container.activeSession !== active) throw CancellationException("Tunnel session changed") }
}
