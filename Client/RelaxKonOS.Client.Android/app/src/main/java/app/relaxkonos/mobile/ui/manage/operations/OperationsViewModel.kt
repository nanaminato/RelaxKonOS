package app.relaxkonos.mobile.ui.manage.operations



import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import android.app.Application
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.PendingInstallationRequest
import app.relaxkonos.mobile.data.ObservedOperation
import app.relaxkonos.mobile.data.OperationCheck
import app.relaxkonos.mobile.data.OperationDomain
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.isActive
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
internal data class OperationsState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val items: List<ObservedOperation> = emptyList(),
    val selectedKey: Pair<OperationDomain, String>? = null,
    val diagnostics: ApiResult<List<String>>? = null,
    val cancelling: Boolean = false,
    val cancelRequested: Boolean = false,
    val hideRequested: Boolean = false,
    val error: Boolean = false,
    val pendingInstallations: List<PendingInstallationRequest> = emptyList(),
    val pendingSites: List<app.relaxkonos.mobile.data.PendingSiteMutation> = emptyList(),
    val pendingCertificates: List<app.relaxkonos.mobile.data.PendingCertificateRequest> = emptyList(),
    val pendingTunnels: List<app.relaxkonos.mobile.data.PendingTunnelMutation> = emptyList(),
    val pendingProxy: List<app.relaxkonos.mobile.data.PendingProxyRequest> = emptyList(),
    val pendingGit: List<app.relaxkonos.mobile.data.PendingGitMutation> = emptyList(),
    val pendingDockerResources: List<app.relaxkonos.mobile.data.PendingDockerResource> = emptyList(),
    val pendingDockerControl: List<app.relaxkonos.mobile.data.PendingDockerControl> = emptyList(),
    val pendingSmb: List<app.relaxkonos.mobile.data.PendingSmbMutation> = emptyList(),
    val pendingFirewall: List<app.relaxkonos.mobile.data.PendingFirewallChange> = emptyList(),
    val pendingWebServers: List<app.relaxkonos.mobile.data.PendingWebServerRequest> = emptyList(),
)

internal class OperationsViewModel(application: Application) : AndroidViewModel(application) {
    suspend fun exportReport(uri: android.net.Uri, report: String): Boolean = try {
        withContext(Dispatchers.IO) {
            val output = getApplication<Application>().contentResolver.openOutputStream(uri, "w")
                ?: throw IllegalStateException("Unable to open diagnostic destination")
            output.use { it.write(report.toByteArray(Charsets.UTF_8)) }
        }
        true
    } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
    catch (_: Exception) { false }


    suspend fun observe(owner: SessionState.Active) {
        while (true) {
            kotlinx.coroutines.delay(5000)
            poll(owner)
        }
    }

    private val container = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(OperationsState())
        private set
    private var refreshJob: Job? = null
    private var generation = 0

    fun stopObserving(owner: SessionState.Active) {
        if (state.owner !== owner) return
        generation++
        refreshJob?.cancel()
        state = OperationsState()
    }

    fun poll(owner: SessionState.Active) {
        if (state.owner !== owner || state.loading || refreshJob?.isActive == true || state.cancelling || state.cancelRequested || state.hideRequested) return
        if (state.items.any { it.check == OperationCheck.Unavailable || it.state in setOf("queued", "running", "cancelling") } || state.error || state.pendingInstallations.isNotEmpty() || state.pendingSites.isNotEmpty() || state.pendingCertificates.isNotEmpty() || state.pendingTunnels.isNotEmpty() || state.pendingProxy.isNotEmpty() || state.pendingWebServers.isNotEmpty() || state.pendingFirewall.isNotEmpty() || state.pendingSmb.isNotEmpty() || state.pendingDockerControl.isNotEmpty() || state.pendingDockerResources.isNotEmpty() || state.pendingGit.isNotEmpty())
            refresh(owner, quiet = true)
    }

    fun recoverInstallation(owner: SessionState.Active, id: String) {
        if (state.owner !== owner || state.loading) return
        val request = generation
        state = state.copy(loading = true)
        viewModelScope.launch {
            try {
                val result = withContext(Dispatchers.IO) { container.installations.recoverById(owner, id.trim()) }
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun refresh(owner: SessionState.Active, cancellationUnverified: Boolean = false, quiet: Boolean = false) {
        refreshJob?.cancel()
        val request = ++generation
        state = if (state.owner === owner) state.copy(loading = !quiet, cancelling = false,
            cancelRequested = false, hideRequested = false, diagnostics = null) else OperationsState(owner = owner, loading = true)
        refreshJob = viewModelScope.launch {
            try {
                val snapshot = withContext(Dispatchers.IO) { container.operationCenter.refresh(owner) }
                val items = snapshot.items
                if (current(owner, request)) {
                    state = state.copy(loading = false, items = items,
                        selectedKey = state.selectedKey?.takeIf { key -> items.any { (it.reference.domain to it.reference.operationId) == key } },
                        error = snapshot.incomplete || cancellationUnverified, pendingInstallations = snapshot.pendingInstallations, pendingSites = snapshot.pendingSites, pendingCertificates = snapshot.pendingCertificates, pendingTunnels = snapshot.pendingTunnels, pendingProxy = snapshot.pendingProxy, pendingWebServers = snapshot.pendingWebServers, pendingFirewall = snapshot.pendingFirewall, pendingSmb = snapshot.pendingSmb, pendingDockerControl = snapshot.pendingDockerControl, pendingDockerResources = snapshot.pendingDockerResources, pendingGit = snapshot.pendingGit)
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun select(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner) return
        val request = generation
        val key = item.reference.domain to item.reference.operationId
        state = state.copy(selectedKey = key, diagnostics = null, cancelRequested = false)
        if (item.check != OperationCheck.Verified || item.reference.domain in setOf(OperationDomain.Website, OperationDomain.Installation)) return
        viewModelScope.launch {
            val result = container.operationCenter.diagnostics(owner, item)
            if (current(owner, request) && state.selectedKey == key) {
                state = state.copy(diagnostics = result)
            }
        }
    }

    fun requestCancel() { state = state.copy(cancelRequested = true) }
    fun dismissCancel() { state = state.copy(cancelRequested = false) }
    fun requestHide() { state = state.copy(hideRequested = true) }
    fun dismissHide() { state = state.copy(hideRequested = false) }

    fun hide(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner) return
        val request = generation
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { container.operationCenter.hide(owner, item) }
                if (current(owner, request)) state = state.copy(
                    items = state.items.filterNot { it.reference == item.reference }, selectedKey = null,
                    hideRequested = false)
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(hideRequested = false, error = true)
            }
        }
    }

    fun cancel(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner || !item.cancellable) return
        val request = generation
        state = state.copy(cancelRequested = false, cancelling = true)
        viewModelScope.launch {
            try {
                val result = container.operationCenter.cancel(owner, item)
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(cancelling = false, error = true)
            }
        }
    }

    private fun current(owner: SessionState.Active, request: Int) =
        state.owner === owner && container.session.state.value === owner && request == generation
}
