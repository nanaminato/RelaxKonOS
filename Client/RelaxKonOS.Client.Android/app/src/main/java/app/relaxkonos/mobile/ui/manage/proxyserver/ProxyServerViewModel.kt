package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.ProxyServerRepository
import app.relaxkonos.mobile.data.OperationIndex
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.OperationDomain
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/**
 * View state of the Proxy Server workspace.
 *
 * The snapshot is the single source of truth: every mutation is answered with an operation, and the
 * workspace reloads only after that operation stops running. Secrets live exclusively in [export],
 * [issued] and [exportOptions]; they are dropped when the user leaves the section or selects another
 * credential, and they are never part of a saved draft.
 */
internal data class ProxyServerState(
    val busy: Boolean = false,
    val snapshot: ApiResult<ProxyServerSnapshot>? = null,
    val releases: ApiResult<List<ProxyServerRelease>>? = null,
    val validation: ProxyServerValidation? = null,
    val exporting: Boolean = false,
    val exportOptions: List<ProxyServerExportOption> = emptyList(),
    val export: ProxyServerExport? = null,
    val issued: ProxyServerSubscriptionIssued? = null,
    val operation: ProxyOperation? = null,
    val operationVerified: Boolean = false,
    val problemCode: String? = null,
    val uncertain: Boolean = false,
    val savedEpoch: Int = 0,
    val observedAtMillis: Long? = null,
) {
    val facts get() = (snapshot as? ApiResult.Success)?.value
    val ready get() = !busy && !uncertain && facts != null && (operation == null || !operation.state.active)
}

internal class ProxyServerViewModel(private val session: AuthSession, private val repository: ProxyServerRepository,
    private val index: OperationIndex) : ViewModel() {
    private val activeSession get() = session.state.value as? SessionState.Active
    private var owner: SessionState.Active? = null
    var disclosureVersion by mutableLongStateOf(0L)
        private set
    private var formatsVersion = 0L
    private var formatsJob: Job? = null

    var state by mutableStateOf(ProxyServerState())
        private set
    var sessionEpoch by mutableIntStateOf(0)
        private set
    var selectedCredentialId by mutableStateOf<String?>(null)
        private set

    init {
        viewModelScope.launch {
            session.state.collect { next ->
                if (next !== owner) {
                    clearSensitiveOutputs()
                    owner = next as? SessionState.Active
                    state = ProxyServerState()
                    selectedCredentialId = null
                    sessionEpoch++
                }
            }
        }
    }

    fun selectCredential(id: String?) {
        if (selectedCredentialId == id) return
        clearSensitiveOutputs()
        selectedCredentialId = id
        // A secret belongs to the credential it was exported from; switching must not leave it on screen.
        state = state.copy(export = null, exportOptions = emptyList())
    }

    fun refresh() = work { active ->
        val snapshot = repository.snapshot(active); verify(active)
        state = state.copy(snapshot = snapshot, observedAtMillis = System.currentTimeMillis())
        failure(snapshot)
        val id = operationId(active)
        if (id != null) operationResult(active, repository.operation(active, id))
    }

    fun loadReleases() = work { active ->
        val result = repository.releases(active); verify(active); failure(result)
        state = state.copy(releases = result)
    }

    fun validate() = work { active ->
        val result = repository.validate(active); verify(active); failure(result)
        state = state.copy(validation = (result as? ApiResult.Success)?.value)
    }

    fun publish() = operation { active -> repository.publish(active) }
    fun rollback() = operation { active -> repository.rollback(active) }
    fun recover() = operation { active -> repository.recover(active) }
    fun runtime(action: ProxyServerRuntimeAction) = operation { active -> repository.runtime(active, action) }
    fun install() = operation { active -> repository.install(active) }
    fun upgrade(version: String) = operation { active -> repository.upgrade(active, version) }
    fun uninstall(keepData: Boolean) = operation { active -> repository.uninstall(active, keepData) }

    fun savePrincipal(id: String?, displayName: String, userId: String?) = operation { active ->
        repository.savePrincipal(active, id, SaveProxyServerPrincipal(id, displayName, userId))
    }

    fun deletePrincipal(id: String) = operation { active -> repository.deletePrincipal(active, id) }

    fun saveCredential(id: String?, principalId: String, name: String, inboundIds: List<String>, enabled: Boolean,
        expiresAtMillis: Long?) = operation { active ->
        repository.saveCredential(active, id,
            SaveProxyServerCredential(id, principalId, name, inboundIds, enabled, expiresAtMillis))
    }

    fun rotateCredential(id: String, enabled: Boolean, expiresAtMillis: Long?) = operation { active ->
        repository.rotateCredential(active, id,
            RotateProxyServerCredential(enabled, expiresAtMillis, clearExpiry = expiresAtMillis == null))
    }

    fun deleteCredential(id: String) = operation { active ->
        if (selectedCredentialId == id) selectCredential(null)
        repository.deleteCredential(active, id)
    }

    fun loadExportFormats(credentialId: String) {
        val active = activeSession ?: return
        if (owner !== active || selectedCredentialId != credentialId) return
        val version = ++formatsVersion
        formatsJob?.cancel()
        state = state.copy(exporting = true, export = null, exportOptions = emptyList())
        formatsJob = viewModelScope.launch {
            fun current() = activeSession === active && owner === active && formatsVersion == version && selectedCredentialId == credentialId
            try {
                val result = repository.exportFormats(active, credentialId)
                verify(active)
                if (current()) {
                    failure(result)
                    state = state.copy(exportOptions = (result as? ApiResult.Success)?.value.orEmpty())
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current()) state = state.copy(uncertain = true)
            } finally {
                if (current()) state = state.copy(exporting = false)
            }
        }
    }

    fun export(expectedOwner: SessionState.Active?, expectedVersion: Long, format: String) {
        if (expectedOwner !== owner || expectedOwner !== activeSession || expectedVersion != disclosureVersion) return
        val credentialId = selectedCredentialId ?: return
        if (state.busy || state.exportOptions.none { it.format == format && it.supported }) return
        val version = ++disclosureVersion
        state = state.copy(export = null)
        work { active ->
            val result = repository.export(active, credentialId, ProxyServerExportRequest(credentialId, format))
            verify(active); failure(result)
            if (version == disclosureVersion && selectedCredentialId == credentialId)
                state = state.copy(export = (result as? ApiResult.Success)?.value)
        }
    }

    fun clearExport() {
        disclosureVersion++
        state = state.copy(export = null)
    }

    fun issueSubscription(expectedOwner: SessionState.Active?, expectedVersion: Long, credentialId: String, formats: List<String>, expiresAtMillis: Long?) {
        if (expectedOwner !== owner || expectedOwner !== activeSession || expectedVersion != disclosureVersion) return
        if (state.busy) return
        val version = ++disclosureVersion
        val requestedFormats = formats.toList()
        state = state.copy(issued = null)
        work { active ->
            val result = repository.issueSubscription(active, IssueProxyServerSubscription(credentialId, requestedFormats, expiresAtMillis))
            verify(active); failure(result)
            if (version == disclosureVersion) state = state.copy(issued = (result as? ApiResult.Success)?.value)
            if (result is ApiResult.Success) load(active)
        }
    }

    /** Revokes display permission even when a request has already reached the server. */
    fun clearSensitiveOutputs() {
        disclosureVersion++
        formatsVersion++
        formatsJob?.cancel()
        formatsJob = null
        selectedCredentialId = null
        state = state.copy(export = null, issued = null, exportOptions = emptyList(), exporting = false)
    }

    fun revokeSubscription(id: String) = operation { active -> repository.revokeSubscription(active, id) }

    fun clearIssued() {
        disclosureVersion++
        state = state.copy(issued = null)
    }

    fun pollOperation() = work { active ->
        val id = state.operation?.operationId ?: return@work
        operationResult(active, repository.operation(active, id))
        if (state.operation?.state?.active == false) load(active)
    }

    fun recoverOperation(id: String) = work { active ->
        operationResult(active, repository.operation(active, id.trim()))
    }

    /** Keeps the operation loop running while a queued host operation is still in flight. */
    suspend fun observeOperation() {
        if (state.operationVerified && state.operation?.state?.active == true && !state.busy) {
            kotlinx.coroutines.delay(1500)
            pollOperation()
        }
    }

    private fun operation(call: suspend (SessionState.Active) -> ApiResult<ProxyOperation>) = work { active ->
        operationResult(active, call(active))
    }

    private suspend fun operationResult(active: SessionState.Active, result: ApiResult<ProxyOperation>) {
        verify(active); failure(result)
        state = state.copy(operationVerified = result is ApiResult.Success,
            operation = (result as? ApiResult.Success)?.value ?: state.operation)
        if (result is ApiResult.Success && !result.value.state.active) load(active)
        if (result is ApiResult.Success && result.value.problemCode.isNotBlank())
            state = state.copy(problemCode = result.value.problemCode)
    }

    private suspend fun load(active: SessionState.Active) {
        val snapshot = repository.snapshot(active); verify(active)
        state = state.copy(snapshot = snapshot, observedAtMillis = System.currentTimeMillis(), savedEpoch = state.savedEpoch + 1)
        failure(snapshot)
    }

    private fun operationId(active: SessionState.Active): String? =
        state.operation?.operationId ?: index.forOwner(active)
            .firstOrNull { it.domain == OperationDomain.ProxyServer }?.operationId
    private fun failure(result: ApiResult<*>) {
        when (result) {
            is ApiResult.Problem -> state = state.copy(problemCode = result.code)
            is ApiResult.Transport -> state = state.copy(uncertain = true)
            is ApiResult.Success -> Unit
        }
    }

    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = activeSession ?: return
        if (owner !== active) {
            owner = active; state = ProxyServerState(); selectedCredentialId = null; sessionEpoch++
        }
        if (state.busy) return
        state = state.copy(busy = true, problemCode = null, uncertain = false)
        viewModelScope.launch {
            try {
                verify(active); block(active)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (activeSession === active) state = state.copy(uncertain = true, operationVerified = false)
            } finally {
                if (activeSession === active) state = state.copy(busy = false)
            }
        }
    }

    private suspend fun verify(active: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (activeSession !== active) throw CancellationException("Proxy Server session changed")
    }
}
