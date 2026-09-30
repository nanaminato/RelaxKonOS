package app.relaxkonos.mobile.ui.manage.websites

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

internal data class NginxState(
    val loading: Boolean = false, val busy: Boolean = false,
    val servers: List<WebServer> = emptyList(), val candidates: List<WebServerCandidate> = emptyList(),
    val statuses: Map<String, WebServerStatus> = emptyMap(), val tests: Map<String, WebServerConfigTest> = emptyMap(),
    val selectedId: String? = null, val catalog: WebServerInstallCatalog? = null,
    val system: HostOperatingSystemKind = HostOperatingSystemKind.Unknown,
    val reference: InstallationFileReference? = null, val uploadBytes: Long? = null,
    val operation: WebServerOperation? = null, val installation: InstallationOperation? = null,
    val pending: List<PendingWebServerRequest> = emptyList(), val pendingInstallation: Boolean = false,
    val sites: Map<String, ApiResult<List<WebServerSite>>> = emptyMap(),
    val certificates: ApiResult<List<ManagedCertificate>>? = null,
    val siteDraft: WebSiteDraft? = null, val initialSiteDraft: WebSiteDraft? = null,
    val pendingSites: List<PendingSiteMutation> = emptyList(),
    val siteFacts: Map<String, ApiResult<List<WebServerSite>>> = emptyMap(),
    val siteSaved: Boolean = false, val siteGeneration: Int = 0,
    val problemCode: String? = null, val uncertain: Boolean = false,
)

internal class NginxViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var owner: SessionState.Active? = null
    var sessionEpoch by mutableIntStateOf(0)
        private set
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(NginxState())
        private set
    init {
        viewModelScope.launch {
            container.session.state.collect { active ->
                if (active !== owner) { owner = active as? SessionState.Active; intent = null; state = NginxState(); sessionEpoch++ }
            }
        }
    }
    fun select(id: String?) { state = state.copy(selectedId = id) }
    fun clearReference() { if (!state.busy && intent == null) state = state.copy(reference = null) }
    fun refresh() = work { active -> load(active) }
    private suspend fun load(active: SessionState.Active) {
        state = state.copy(loading = true)
        val servers = container.webServers.discover(active)
        val candidates = container.webServers.candidates(active)
        val system = container.gateway.hostOperatingSystem(active.effectiveBaseUrl)
        val catalog = if ((system as? ApiResult.Success)?.value in windowsSystems) container.webServers.catalog(active) else null
        val statuses = mutableMapOf<String, WebServerStatus>()
        val tests = mutableMapOf<String, WebServerConfigTest>()
        val sites = mutableMapOf<String, ApiResult<List<WebServerSite>>>()
        if (servers is ApiResult.Success) servers.value.forEach { server ->
            (container.webPublishing.status(active, server.id) as? ApiResult.Success)?.value?.let { statuses[server.id] = it }
            if (server.canRead) sites[server.id] = container.webSites.sites(active, server.id)
            if (server.canTestConfiguration) (container.webPublishing.configTest(active, server.id) as? ApiResult.Success)?.value?.let { tests[server.id] = it }
        }
        val certificates = if (ServerCapabilities.CERTIFICATES in active.capabilities) container.webPublishing.certificates(active) else null
        val installation = if (active.privilegedOperations) container.installations.active(active, InstallationService.Nginx) else null
        verify(active)
        val values = (servers as? ApiResult.Success)?.value.orEmpty()
        state = state.copy(loading = false, servers = values,
            sites = sites, certificates = certificates,
            pendingSites = container.webSites.pending(active),
            candidates = (candidates as? ApiResult.Success)?.value.orEmpty(), statuses = statuses, tests = tests,
            selectedId = state.selectedId?.takeIf { id -> values.any { it.id == id } },
            catalog = (catalog as? ApiResult.Success)?.value, system = (system as? ApiResult.Success)?.value ?: HostOperatingSystemKind.Unknown,
            installation = (installation as? ApiResult.Success)?.value ?: state.installation,
            pending = container.webServers.pending(active),
            pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Nginx },
            problemCode = listOf(servers, candidates, catalog).filterIsInstance<ApiResult.Problem>().firstOrNull()?.code,
            uncertain = servers !is ApiResult.Success || candidates !is ApiResult.Success || system !is ApiResult.Success ||
                (catalog != null && catalog !is ApiResult.Success) || (installation != null && installation !is ApiResult.Success))
        if (state.operation == null) container.operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.WebServer }?.let { record ->
            val result = container.webServers.operation(active, record.operationId)
            verify(active)
            if (result is ApiResult.Success) state = state.copy(operation = result.value)
        }
    }
    fun refreshCertificates() = work { active ->
        val result = if (ServerCapabilities.CERTIFICATES in active.capabilities) container.certificates.list(active) else null
        verify(active); state = state.copy(certificates = result)
    }
    fun editSite(server: WebServer, site: WebServerSite? = null) {
        if (state.busy) return
        val draft = site?.let(WebSiteDraft::from) ?: WebSiteDraft(server.id)
        state = state.copy(siteDraft = draft, initialSiteDraft = draft, siteSaved = false, problemCode = null, uncertain = false)
    }
    fun updateSiteDraft(draft: WebSiteDraft) {
        if (!state.busy && state.pendingSites.none { it.serverId == draft.serverId && it.siteId == draft.id }) state = state.copy(siteDraft = draft)
    }
    fun closeSiteDraft() { if (!state.busy) state = state.copy(siteDraft = null, initialSiteDraft = null) }
    fun saveSite() = work { active ->
        val draft = state.siteDraft ?: return@work
        val request = draft.request() ?: return@work
        if (draft.httpsEnabled && !draft.useServerCertificate) {
            if (ServerCapabilities.CERTIFICATES !in active.capabilities) { state = state.copy(problemCode = "webserver.site_certificate_unverified"); return@work }
            val certificate = container.certificates.certificate(active, requireNotNull(draft.certificateId)); verify(active)
            if (certificate !is ApiResult.Success || certificate.value.usageProblem(draft.bindings.map { it.domain }) != null) {
                state = state.copy(problemCode = "webserver.site_certificate_unverified"); return@work
            }
        }
        val result = container.webSites.save(active, draft.serverId, request, container.elevationAnswers)
        verify(active); failure(result)
        state = state.copy(pendingSites = container.webSites.pending(active))
        if (result is ApiResult.Success) {
            state = state.copy(siteDraft = null, initialSiteDraft = null, siteSaved = true, siteGeneration = state.siteGeneration + 1)
            reloadSites(active, draft.serverId)
        }
    }
    fun deleteSite(site: WebServerSite) = work { active ->
        val result = container.webSites.delete(active, site, container.elevationAnswers)
        verify(active); failure(result)
        state = state.copy(pendingSites = container.webSites.pending(active))
        if (result is ApiResult.Success) { state = state.copy(siteSaved = true, siteGeneration = state.siteGeneration + 1); reloadSites(active, site.serverId) }
    }
    fun inspectSites(serverId: String) = work { active -> reloadSites(active, serverId) }
    private suspend fun reloadSites(active: SessionState.Active, serverId: String) {
        val result = container.webSites.sites(active, serverId)
        verify(active)
        state = state.copy(sites = state.sites + (serverId to result), siteFacts = state.siteFacts + (serverId to result))
        if (result !is ApiResult.Success) failure(result)
    }
    fun acceptSiteFacts(pending: PendingSiteMutation) = work { active ->
        val result = container.webSites.acceptFacts(active, pending)
        verify(active); failure(result)
        state = state.copy(pendingSites = container.webSites.pending(active), siteFacts = state.siteFacts + (pending.serverId to result), sites = state.sites + (pending.serverId to result))
        if (result is ApiResult.Success && state.siteDraft?.id == pending.siteId && state.siteDraft?.serverId == pending.serverId) state = state.copy(siteDraft = null, initialSiteDraft = null)
    }
    fun reloadSiteDraft() {
        val draft = state.siteDraft ?: return
        val current = (state.sites[draft.serverId] as? ApiResult.Success)?.value?.firstOrNull { it.id == draft.id } ?: return
        val refreshed = WebSiteDraft.from(current)
        if (!state.busy && state.pendingSites.none { it.siteId == draft.id && it.serverId == draft.serverId }) state = state.copy(siteDraft = refreshed, initialSiteDraft = refreshed, problemCode = null, uncertain = false)
    }
    fun fileReference(path: String) = work { active ->
        val result = container.installations.fileReference(active, InstallationService.Nginx, path)
        verify(active)
        referenceResult(result)
    }
    fun upload(uri: Uri) = work { active ->
        val document = container.uploadDocuments.open(uri.toString())
        if (document == null) { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
        val result = container.installations.upload(active, InstallationService.Nginx, document) { bytes ->
            // API progress runs on IO; marshal Compose state changes back to the ViewModel dispatcher.
            viewModelScope.launch { if (container.activeSession === active && state.busy) state = state.copy(uploadBytes = bytes) }
        }
        verify(active); referenceResult(result)
    }
    private fun referenceResult(result: ApiResult<InstallationFileReference>) {
        state = state.copy(reference = (result as? ApiResult.Success)?.value, uploadBytes = null)
        failure(result)
    }
    fun install(version: String?, packageSource: Boolean, kind: InstallationKind = InstallationKind.Install) = work { active ->
        if (intent == null) {
            val reference = state.reference
            if (packageSource && (reference == null || reference.expired())) {
                state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work
            }
            intent = container.installations.prepare(active, kind, NginxInstallationRequest(true,
                version?.trim()?.takeIf(String::isNotBlank), if (packageSource) reference?.id else null))
        }
        val result = container.installations.submit(requireNotNull(intent), container.elevationAnswers)
        verify(active)
        failure(result)
        state = state.copy(pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Nginx })
        if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
        if (result is ApiResult.Success) {
            intent = null
            state = state.copy(installation = result.value, reference = null, pendingInstallation = false)
        }
    }
    fun integrate(candidate: WebServerCandidate) = webMutation { active -> container.webServers.integrate(active, candidate, container.elevationAnswers) }
    fun lifecycle(server: WebServer, action: WebServerAction) = webMutation { active -> container.webServers.lifecycle(active, server, action, container.elevationAnswers) }
    fun resume(pending: PendingWebServerRequest) = webMutation { active -> container.webServers.resume(active, pending, container.elevationAnswers) }
    fun recover(id: String) = webMutation { active -> container.webServers.recoverById(active, id.trim()) }
    private fun webMutation(call: suspend (SessionState.Active) -> ApiResult<WebServerOperation>) = work { active ->
        val result = call(active); verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(operation = result.value)
        state = state.copy(pending = container.webServers.pending(active))
    }
    fun pollTasks() = work { active ->
        state.operation?.takeIf { it.state.active }?.let { operation ->
            val result = container.webServers.operation(active, operation.operationId)
            verify(active); failure(result)
            if (result is ApiResult.Success) state = state.copy(operation = result.value)
        }
        state.installation?.takeIf { it.state.active }?.let { operation ->
            val result = container.installations.operation(active, operation.operationId)
            verify(active); failure(result)
            if (result is ApiResult.Success) state = state.copy(installation = result.value)
        }
    }
    fun pollWeb() = webMutation { active -> container.webServers.operation(active, requireNotNull(state.operation).operationId) }
    fun cancelWeb() = webMutation { active -> container.webServers.cancel(active, requireNotNull(state.operation).operationId) }
    fun pollInstallation() = work { active ->
        val result = container.installations.operation(active, requireNotNull(state.installation).operationId)
        verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(installation = result.value)
    }
    fun cancelInstallation() = work { active ->
        val result = container.installations.cancel(active, requireNotNull(state.installation).operationId)
        verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(installation = result.value)
    }
    fun retryInstallation() { if (intent != null) install(null, false) }
    val intentVersion get() = (intent?.request as? NginxInstallationRequest)?.version
    val intentUsesPackage get() = (intent?.request as? NginxInstallationRequest)?.fileReferenceId != null
    val hasIntent get() = intent != null
    private fun failure(result: ApiResult<*>) {
        state = state.copy(problemCode = (result as? ApiResult.Problem)?.code, uncertain = result is ApiResult.Transport)
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = container.activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = NginxState(); sessionEpoch++ }
        if (state.busy || state.loading) return
        state = state.copy(busy = true, problemCode = null, uncertain = false)
        viewModelScope.launch {
            try { block(active) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                if (container.activeSession === active) state = state.copy(uncertain = true,
                    pendingSites = runCatching { container.webSites.pending(active) }.getOrDefault(state.pendingSites))
            }
            finally { if (container.activeSession === active) state = state.copy(busy = false, loading = false) }
        }
    }
    private fun verify(active: SessionState.Active) { if (container.activeSession !== active) throw CancellationException("Nginx session changed") }
    companion object { val windowsSystems = setOf(HostOperatingSystemKind.Windows10, HostOperatingSystemKind.Windows11, HostOperatingSystemKind.WindowsServer) }
}
