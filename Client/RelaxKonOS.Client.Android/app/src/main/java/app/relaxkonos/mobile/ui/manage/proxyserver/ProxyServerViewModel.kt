package app.relaxkonos.mobile.ui.manage.proxyserver

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.OperationDomain
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

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

internal class ProxyServerViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var owner: SessionState.Active? = null

    var state by mutableStateOf(ProxyServerState())
        private set
    var sessionEpoch by mutableIntStateOf(0)
        private set
    var selectedCredentialId by mutableStateOf<String?>(null)
        private set

    init {
        viewModelScope.launch {
            container.session.state.collect { next ->
                if (next !== owner) {
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
        selectedCredentialId = id
        // A secret belongs to the credential it was exported from; switching must not leave it on screen.
        state = state.copy(export = null, exportOptions = emptyList())
    }

    fun refresh() = work { active ->
        val snapshot = container.proxyServer.snapshot(active); verify(active)
        state = state.copy(snapshot = snapshot, observedAtMillis = System.currentTimeMillis())
        failure(snapshot)
        val id = operationId(active)
        if (id != null) operationResult(active, container.proxyServer.operation(active, id))
    }

    fun loadReleases() = work { active ->
        val result = container.proxyServer.releases(active); verify(active); failure(result)
        state = state.copy(releases = result)
    }

    fun validate() = work { active ->
        val result = container.proxyServer.validate(active); verify(active); failure(result)
        state = state.copy(validation = (result as? ApiResult.Success)?.value)
    }

    fun publish() = operation { active -> container.proxyServer.publish(active) }
    fun rollback() = operation { active -> container.proxyServer.rollback(active) }
    fun recover() = operation { active -> container.proxyServer.recover(active) }
    fun runtime(action: ProxyServerRuntimeAction) = operation { active -> container.proxyServer.runtime(active, action) }
    fun install() = operation { active -> container.proxyServer.install(active) }
    fun upgrade(version: String) = operation { active -> container.proxyServer.upgrade(active, version) }
    fun uninstall(keepData: Boolean) = operation { active -> container.proxyServer.uninstall(active, keepData) }

    fun savePrincipal(id: String?, displayName: String, userId: String?) = operation { active ->
        container.proxyServer.savePrincipal(active, id, SaveProxyServerPrincipal(id, displayName, userId))
    }

    fun deletePrincipal(id: String) = operation { active -> container.proxyServer.deletePrincipal(active, id) }

    fun saveCredential(id: String?, principalId: String, name: String, inboundIds: List<String>, enabled: Boolean,
        expiresAtMillis: Long?) = operation { active ->
        container.proxyServer.saveCredential(active, id,
            SaveProxyServerCredential(id, principalId, name, inboundIds, enabled, expiresAtMillis))
    }

    fun rotateCredential(id: String, enabled: Boolean, expiresAtMillis: Long?) = operation { active ->
        container.proxyServer.rotateCredential(active, id,
            RotateProxyServerCredential(enabled, expiresAtMillis, clearExpiry = expiresAtMillis == null))
    }

    fun deleteCredential(id: String) = operation { active ->
        if (selectedCredentialId == id) selectCredential(null)
        container.proxyServer.deleteCredential(active, id)
    }

    fun loadExportFormats(credentialId: String) {
        val active = container.activeSession ?: return
        if (owner !== active) return
        if (state.exporting) return
        state = state.copy(exporting = true, export = null)
        viewModelScope.launch {
            try {
                val result = container.proxyServer.exportFormats(active, credentialId); verify(active); failure(result)
                state = state.copy(exportOptions = (result as? ApiResult.Success)?.value.orEmpty())
            } finally {
                if (container.activeSession === active) state = state.copy(exporting = false)
            }
        }
    }

    fun export(format: String) = work { active ->
        val credentialId = selectedCredentialId ?: return@work
        val result = container.proxyServer.export(active, credentialId, ProxyServerExportRequest(credentialId, format))
        verify(active); failure(result)
        state = state.copy(export = (result as? ApiResult.Success)?.value)
    }

    fun clearExport() {
        if (!state.busy) state = state.copy(export = null)
    }

    fun issueSubscription(credentialId: String, formats: List<String>, expiresAtMillis: Long?) = work { active ->
        val result = container.proxyServer.issueSubscription(active, IssueProxyServerSubscription(credentialId, formats, expiresAtMillis))
        verify(active); failure(result)
        state = state.copy(issued = (result as? ApiResult.Success)?.value)
        if (result is ApiResult.Success) load(active)
    }

    fun revokeSubscription(id: String) = operation { active -> container.proxyServer.revokeSubscription(active, id) }

    fun clearIssued() {
        if (!state.busy) state = state.copy(issued = null)
    }

    fun pollOperation() = work { active ->
        val id = state.operation?.operationId ?: return@work
        operationResult(active, container.proxyServer.operation(active, id))
        if (state.operation?.state?.active == false) load(active)
    }

    fun recoverOperation(id: String) = work { active ->
        operationResult(active, container.proxyServer.operation(active, id.trim()))
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
        val snapshot = container.proxyServer.snapshot(active); verify(active)
        state = state.copy(snapshot = snapshot, observedAtMillis = System.currentTimeMillis(), savedEpoch = state.savedEpoch + 1)
        failure(snapshot)
    }

    private fun operationId(active: SessionState.Active): String? =
        state.operation?.operationId ?: container.operationIndex.forOwner(active)
            .firstOrNull { it.domain == OperationDomain.ProxyServer }?.operationId
    private fun failure(result: ApiResult<*>) {
        when (result) {
            is ApiResult.Problem -> state = state.copy(problemCode = result.code)
            is ApiResult.Transport -> state = state.copy(uncertain = true)
            is ApiResult.Success -> Unit
        }
    }

    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = container.activeSession ?: return
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
                if (container.activeSession === active) state = state.copy(uncertain = true, operationVerified = false)
            } finally {
                if (container.activeSession === active) state = state.copy(busy = false)
            }
        }
    }

    private fun verify(active: SessionState.Active) {
        if (container.activeSession !== active) throw CancellationException("Proxy Server session changed")
    }
}
