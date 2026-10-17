package app.relaxkonos.mobile.ui.manage.operations



import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
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
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
internal class OperationReview(val item: ObservedOperation)

internal data class OperationsState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val items: List<ObservedOperation> = emptyList(),
    val selectedKey: Pair<OperationDomain, String>? = null,
    val diagnostics: ApiResult<List<String>>? = null,
    val diagnosticsLoading: Boolean = false,
    val cancelling: Boolean = false,
    val cancelReview: OperationReview? = null,
    val hideReview: OperationReview? = null,
    val hiding: Boolean = false,
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

internal class OperationsViewModel(private val session: AuthSession, private val actions: OperationsActions,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO) : ViewModel() {
    suspend fun exportReport(owner: SessionState.Active, uri: android.net.Uri, report: String): Boolean {
        if (session.state.value !== owner || state.owner !== owner) return false
        return actions.exportReport(uri, report)
    }


    suspend fun observe(owner: SessionState.Active, canObserve: () -> Boolean) {
        fun eligible() = session.state.value === owner && state.owner === owner && canObserve()
        while (eligible()) {
            kotlinx.coroutines.delay(5000)
            if (!eligible()) return
            poll(owner)
        }
    }

    var state by mutableStateOf(OperationsState())
        private set
    private var refreshJob: Job? = null
    private var generation = 0
    private var diagnosticGeneration = 0L

    init {
        viewModelScope.launch {
            session.state.collect { next ->
                if (state.owner != null && state.owner !== next) clearState()
            }
        }
    }

    private fun clearState() {
        generation++; diagnosticGeneration++
        refreshJob?.cancel()
        state = OperationsState()
    }

    fun stopObserving(owner: SessionState.Active) {
        if (state.owner !== owner) return
        clearState()
    }

    fun poll(owner: SessionState.Active) {
        if (session.state.value !== owner || state.owner !== owner || state.loading || refreshJob?.isActive == true || state.cancelling || state.hiding || state.cancelReview != null || state.hideReview != null) return
        if (state.items.any { it.check == OperationCheck.Unavailable || it.state in setOf("queued", "running", "cancelling") } || state.error || state.pendingInstallations.isNotEmpty() || state.pendingSites.isNotEmpty() || state.pendingCertificates.isNotEmpty() || state.pendingTunnels.isNotEmpty() || state.pendingProxy.isNotEmpty() || state.pendingWebServers.isNotEmpty() || state.pendingFirewall.isNotEmpty() || state.pendingSmb.isNotEmpty() || state.pendingDockerControl.isNotEmpty() || state.pendingDockerResources.isNotEmpty() || state.pendingGit.isNotEmpty())
            refresh(owner, quiet = true)
    }

    fun recoverInstallation(owner: SessionState.Active, id: String) {
        if (session.state.value !== owner || state.owner !== owner || state.loading) return
        val request = generation
        state = state.copy(loading = true)
        viewModelScope.launch {
            try {
                val result = withContext(ioDispatcher) { actions.recoverInstallation(owner, id.trim()) }
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun refresh(owner: SessionState.Active, cancellationUnverified: Boolean = false, quiet: Boolean = false) {
        if (session.state.value !== owner) return
        refreshJob?.cancel()
        val request = ++generation
        val retainDiagnostics = quiet && state.owner === owner
        if (!retainDiagnostics) diagnosticGeneration++
        state = if (state.owner === owner) state.copy(loading = !quiet, cancelling = false,
            cancelReview = null, hideReview = null, hiding = false,
            diagnostics = state.diagnostics.takeIf { retainDiagnostics },
            diagnosticsLoading = retainDiagnostics && state.diagnosticsLoading) else OperationsState(owner = owner, loading = true)
        refreshJob = viewModelScope.launch {
            try {
                val snapshot = withContext(ioDispatcher) { actions.refresh(owner) }
                val items = snapshot.items
                if (current(owner, request)) {
                    state = state.copy(loading = false, items = items,
                        selectedKey = state.selectedKey?.takeIf { key -> items.any { (it.reference.domain to it.reference.operationId) == key } },
                        error = snapshot.incomplete || cancellationUnverified, pendingInstallations = snapshot.pendingInstallations, pendingSites = snapshot.pendingSites, pendingCertificates = snapshot.pendingCertificates, pendingTunnels = snapshot.pendingTunnels, pendingProxy = snapshot.pendingProxy, pendingWebServers = snapshot.pendingWebServers, pendingFirewall = snapshot.pendingFirewall, pendingSmb = snapshot.pendingSmb, pendingDockerControl = snapshot.pendingDockerControl, pendingDockerResources = snapshot.pendingDockerResources, pendingGit = snapshot.pendingGit)
                    val selected = items.firstOrNull { (it.reference.domain to it.reference.operationId) == state.selectedKey }
                    if (selected?.check != OperationCheck.Verified) {
                        diagnosticGeneration++
                        state = state.copy(diagnostics = null, diagnosticsLoading = false)
                    } else if (!quiet) select(owner, selected)
                }
            } catch (cancelled: CancellationException) {
                if (current(owner, request)) invalidateFacts()
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) invalidateFacts()
            }
        }
    }

    fun select(owner: SessionState.Active, item: ObservedOperation) {
        if (session.state.value !== owner || state.owner !== owner || state.loading || state.cancelling || state.hiding || item !in state.items) return
        val selection = ++diagnosticGeneration
        val key = item.reference.domain to item.reference.operationId
        val readable = item.check == OperationCheck.Verified && item.reference.domain !in setOf(OperationDomain.Website, OperationDomain.Installation)
        state = state.copy(selectedKey = key, diagnostics = null, diagnosticsLoading = readable, cancelReview = null, hideReview = null)
        if (!readable) return
        fun selected() = session.state.value === owner && state.owner === owner && state.selectedKey == key && diagnosticGeneration == selection
        viewModelScope.launch {
            try {
                val result = actions.diagnostics(owner, item)
                if (selected()) state = state.copy(diagnostics = result, diagnosticsLoading = false)
            } catch (cancelled: CancellationException) {
                if (selected()) state = state.copy(diagnostics = ApiResult.Transport(null), diagnosticsLoading = false)
                throw cancelled
            } catch (_: Exception) {
                if (selected()) state = state.copy(diagnostics = ApiResult.Transport(null), diagnosticsLoading = false)
            }
        }
    }

    private fun reviewable(owner: SessionState.Active, item: ObservedOperation) =
        session.state.value === owner && state.owner === owner && !state.loading && !state.cancelling && !state.hiding &&
            item in state.items && state.selectedKey == (item.reference.domain to item.reference.operationId)
    fun requestCancel(owner: SessionState.Active, item: ObservedOperation) {
        if (reviewable(owner, item) && item.check == OperationCheck.Verified && item.cancellable)
            state = state.copy(cancelReview = OperationReview(item), hideReview = null)
    }
    fun dismissCancel() { state = state.copy(cancelReview = null) }
    fun requestHide(owner: SessionState.Active, item: ObservedOperation) {
        if (reviewable(owner, item)) state = state.copy(hideReview = OperationReview(item), cancelReview = null)
    }
    fun dismissHide() { state = state.copy(hideReview = null) }

    fun hide(owner: SessionState.Active, review: OperationReview) {
        val item = review.item
        if (!reviewable(owner, item) || state.hideReview !== review) return
        val request = generation
        state = state.copy(hideReview = null, hiding = true)
        viewModelScope.launch {
            try {
                withContext(ioDispatcher) { actions.hide(owner, item) }
                if (current(owner, request)) state = state.copy(
                    items = state.items.filterNot { it.reference == item.reference }, selectedKey = null,
                    hiding = false, diagnostics = null, diagnosticsLoading = false)
            } catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(hiding = false, error = true)
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(hiding = false, error = true)
            }
        }
    }

    fun cancel(owner: SessionState.Active, review: OperationReview) {
        val item = review.item
        if (!reviewable(owner, item) || state.cancelReview !== review || !item.cancellable || item.check != OperationCheck.Verified) return
        val request = generation
        state = state.copy(cancelReview = null, cancelling = true)
        viewModelScope.launch {
            try {
                val result = actions.cancel(owner, item)
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                if (current(owner, request)) invalidateFacts()
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) invalidateFacts()
            }
        }
    }

    private fun invalidateFacts() {
        diagnosticGeneration++
        state = state.copy(loading = false, cancelling = false, hiding = false, cancelReview = null, hideReview = null,
            error = true, diagnostics = null, diagnosticsLoading = false,
            items = state.items.map { it.copy(check = OperationCheck.Unavailable, cancellable = false, checkedAtMillis = null) })
    }

    private fun current(owner: SessionState.Active, request: Int) =
        state.owner === owner && session.state.value === owner && request == generation
}
