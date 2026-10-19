package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.core.auth.AuthSession
import android.net.Uri
import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class NginxState(
    val requestVersion: Long = 0,
    val loading: Boolean = false, val busy: Boolean = false,
    val servers: List<WebServer> = emptyList(), val candidates: List<WebServerCandidate> = emptyList(),
    val statuses: Map<String, WebServerStatus> = emptyMap(), val tests: Map<String, WebServerConfigTest> = emptyMap(),
    val selectedId: String? = null, val catalog: WebServerInstallCatalog? = null,
    val download: ApiResult<WebServerInstallDownload>? = null,
    val system: HostOperatingSystemKind = HostOperatingSystemKind.Unknown,
    val reference: InstallationFileReference? = null, val uploadBytes: Long? = null,
    val operation: WebServerOperation? = null, val installation: InstallationOperation? = null,
    val operationVerified: Boolean = false, val installationVerified: Boolean = false,
    val pending: List<PendingWebServerRequest> = emptyList(), val pendingInstallation: Boolean = false,
    val sites: Map<String, ApiResult<List<WebServerSite>>> = emptyMap(),
    val certificates: ApiResult<List<ManagedCertificate>>? = null,
    val siteDraft: WebSiteDraft? = null, val initialSiteDraft: WebSiteDraft? = null,
    val pendingSites: List<PendingSiteMutation> = emptyList(),
    val siteFacts: Map<String, ApiResult<List<WebServerSite>>> = emptyMap(),
    val siteSaved: Boolean = false, val siteGeneration: Int = 0,
    val problemCode: String? = null, val uncertain: Boolean = false,
)

internal sealed class NginxCancellation(val owner: SessionState.Active, val id: String) {
    class Web(owner: SessionState.Active, val operation: WebServerOperation) : NginxCancellation(owner, operation.operationId)
    class Installation(owner: SessionState.Active, val operation: InstallationOperation) : NginxCancellation(owner, operation.operationId)
}

internal class NginxViewModel(private val session: AuthSession, private val gateway: RelaxKonGateway,
    private val webServers: WebServerRepository, private val webPublishing: WebPublishingRepository,
    private val webSites: WebSiteRepository, private val certificates: CertificateRepository,
    private val installations: InstallationRepository, private val operationIndex: OperationIndex,
    private val elevationAnswers: ElevationAnswerProvider, private val uploadDocuments: UploadDocumentOpener) : ViewModel() {
    private val activeSession get() = session.state.value as? SessionState.Active
    private var workGeneration = 0L
    var cancellation by mutableStateOf<NginxCancellation?>(null); private set
    fun dismissCancellation() { cancellation = null }
    fun requestCancellation(review: NginxCancellation) {
        if (validCancellation(review)) cancellation = review
    }
    private fun validCancellation(review: NginxCancellation): Boolean =
        activeSession === review.owner && owner === review.owner && review.owner.privilegedOperations && !state.busy && !state.loading &&
            when (review) {
                is NginxCancellation.Web -> state.operationVerified && state.operation == review.operation && review.operation.state.active
                is NginxCancellation.Installation -> state.installationVerified && state.installation == review.operation && review.operation.state.active && review.operation.cancellable
            }
    fun cancel(review: NginxCancellation) {
        if (cancellation !== review) return
        dismissCancellation()
        if (!validCancellation(review)) return
        when (review) {
            is NginxCancellation.Web -> webMutation { active -> webServers.cancel(active, review.id) }
            is NginxCancellation.Installation -> work { active ->
                state = state.copy(installationVerified = false)
                val result = installations.cancel(active, review.id)
                verify(active); failure(result)
                if (result is ApiResult.Success) {
                    state = state.copy(installation = result.value, installationVerified = true)
                    if (!result.value.state.active) load(active)
                }
            }
        }
    }
    suspend fun observeTasks(canObserve: () -> Boolean) {
        val active = activeSession ?: return
        val generation = workGeneration
        if (!canObserveTasks() || !canObserve()) return
        kotlinx.coroutines.delay(1500)
        if (activeSession === active && owner === active && generation == workGeneration && canObserveTasks() && canObserve()) pollTasks()
    }
    private fun canObserveTasks() = !state.busy && !state.loading && !state.uncertain && state.problemCode == null &&
        cancellation == null && state.siteDraft == null &&
        (state.operationVerified && state.operation?.state?.active == true || state.installationVerified && state.installation?.state?.active == true)
    private var owner: SessionState.Active? = null
    var sessionEpoch by mutableIntStateOf(0)
        private set
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(NginxState())
        private set
    init {
        viewModelScope.launch {
            session.state.collect { active ->
                if (active !== owner) { dismissCancellation(); owner = active as? SessionState.Active; intent = null; state = NginxState(); sessionEpoch++ }
            }
        }
    }
    fun select(id: String?) { if (!state.busy) { dismissCancellation(); state = state.copy(selectedId = id) } }
    fun clearReference() { if (!state.busy && intent == null) state = state.copy(reference = null) }
    fun download(version: String) = work { active ->
        val result = webServers.download(active, version); verify(active); failure(result); state = state.copy(download = result)
    }
    fun refresh() = work { active -> load(active) }
    private suspend fun load(active: SessionState.Active) {
        NginxDiagnostics.event("load.begin epoch=$sessionEpoch web=${state.operation?.state} install=${state.installation?.state}")
        state = state.copy(loading = true, operationVerified = false, installationVerified = false)
        val servers = webServers.discover(active); verify(active)
        val candidates = webServers.candidates(active); verify(active)
        val system = gateway.hostOperatingSystem(active.effectiveBaseUrl); verify(active)
        val catalog = if ((system as? ApiResult.Success)?.value in windowsSystems) webServers.catalog(active) else null
        val statuses = mutableMapOf<String, WebServerStatus>()
        val tests = mutableMapOf<String, WebServerConfigTest>()
        val sites = mutableMapOf<String, ApiResult<List<WebServerSite>>>()
        if (servers is ApiResult.Success) servers.value.forEach { server ->
            (webPublishing.status(active, server.id) as? ApiResult.Success)?.value?.let { statuses[server.id] = it }
            if (server.canRead) sites[server.id] = webSites.sites(active, server.id)
            if (server.canTestConfiguration) (webPublishing.configTest(active, server.id) as? ApiResult.Success)?.value?.let { tests[server.id] = it }
        }
        val certificates = if (ServerCapabilities.CERTIFICATES in active.capabilities) webPublishing.certificates(active) else null
        var installation = if (active.privilegedOperations) installations.active(active, InstallationService.Nginx) else null
        if (installation is ApiResult.Success && installation.value == null) {
            val knownId = state.installation?.operationId ?: operationIndex.forOwner(active)
                .firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Nginx.name }?.operationId
            if (knownId != null) installation = installations.operation(active, knownId)
        }
        // Refresh existing IDs too: the operation center may have observed completion while this
        // ViewModel still holds the original queued/running response.
        val webId = state.operation?.operationId ?: operationIndex.forOwner(active)
            .firstOrNull { it.domain == OperationDomain.WebServer }?.operationId
        val webOperation = webId?.let { webServers.operation(active, it) }
        val installationPending = installations.pending(active).filter { it.service == InstallationService.Nginx }
        for (pending in installationPending.filter { it.operationId != null }) {
            installations.recover(active, pending)
        }
        NginxDiagnostics.event("load.results servers=${NginxDiagnostics.result(servers)} candidates=${NginxDiagnostics.result(candidates)} system=${NginxDiagnostics.result(system)} installation=${NginxDiagnostics.result(installation)} web=${NginxDiagnostics.result(webOperation)}")
        verify(active)
        val values = (servers as? ApiResult.Success)?.value.orEmpty()
        if (active.privilegedOperations) {
            val refreshedWeb = (webOperation as? ApiResult.Success)?.value
            val refreshedInstallation = (installation as? ApiResult.Success)?.value
            webServers.reconcileIntegrationIntents(active, values, tests,
                factsVerified = servers is ApiResult.Success && candidates is ApiResult.Success && installation is ApiResult.Success &&
                    (webId == null || webOperation is ApiResult.Success),
                tasksActive = refreshedWeb?.state?.active == true || refreshedInstallation?.state?.active == true ||
                    installations.pending(active).any { it.service == InstallationService.Nginx } || intent != null)
        }
        state = state.copy(loading = false, servers = values,
            sites = sites, certificates = certificates,
            pendingSites = webSites.pending(active),
            candidates = (candidates as? ApiResult.Success)?.value.orEmpty(), statuses = statuses, tests = tests,
            selectedId = state.selectedId?.takeIf { id -> values.any { it.id == id } },
            catalog = (catalog as? ApiResult.Success)?.value, system = (system as? ApiResult.Success)?.value ?: HostOperatingSystemKind.Unknown,
            installation = if (installation is ApiResult.Success) installation.value else state.installation,
            operation = (webOperation as? ApiResult.Success)?.value ?: state.operation,
            operationVerified = webOperation is ApiResult.Success,
            installationVerified = installation is ApiResult.Success && installation.value != null,
            pending = webServers.pending(active),
            pendingInstallation = installations.pending(active).any { it.service == InstallationService.Nginx },
            problemCode = listOf(servers, candidates, catalog).filterIsInstance<ApiResult.Problem>().firstOrNull()?.code,
            uncertain = servers !is ApiResult.Success || candidates !is ApiResult.Success || system !is ApiResult.Success ||
                (catalog != null && catalog !is ApiResult.Success) || (installation != null && installation !is ApiResult.Success) ||
                (webOperation != null && webOperation !is ApiResult.Success))

    }
    fun refreshCertificates() = work { active ->
        val result = if (ServerCapabilities.CERTIFICATES in active.capabilities) certificates.list(active) else null
        verify(active); state = state.copy(certificates = result)
    }
    fun editSite(server: WebServer, site: WebServerSite? = null) {
        if (state.busy) return
        dismissCancellation()
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
            val certificate = certificates.certificate(active, requireNotNull(draft.certificateId)); verify(active)
            if (certificate !is ApiResult.Success || certificate.value.usageProblem(draft.bindings.map { it.domain }) != null) {
                state = state.copy(problemCode = "webserver.site_certificate_unverified"); return@work
            }
        }
        val result = webSites.save(active, draft.serverId, request, elevationAnswers)
        verify(active); failure(result)
        state = state.copy(pendingSites = webSites.pending(active))
        if (result is ApiResult.Success) {
            state = state.copy(siteDraft = null, initialSiteDraft = null, siteSaved = true, siteGeneration = state.siteGeneration + 1)
            reloadSites(active, draft.serverId)
        }
    }
    fun deleteSite(site: WebServerSite) = work { active ->
        val result = webSites.delete(active, site, elevationAnswers)
        verify(active); failure(result)
        state = state.copy(pendingSites = webSites.pending(active))
        if (result is ApiResult.Success) { state = state.copy(siteSaved = true, siteGeneration = state.siteGeneration + 1); reloadSites(active, site.serverId) }
    }
    fun inspectSites(serverId: String) = work { active -> reloadSites(active, serverId) }
    private suspend fun reloadSites(active: SessionState.Active, serverId: String) {
        val result = webSites.sites(active, serverId)
        verify(active)
        state = state.copy(sites = state.sites + (serverId to result), siteFacts = state.siteFacts + (serverId to result))
        if (result !is ApiResult.Success) failure(result)
    }
    fun acceptSiteFacts(pending: PendingSiteMutation) = work { active ->
        val result = webSites.acceptFacts(active, pending)
        verify(active); failure(result)
        state = state.copy(pendingSites = webSites.pending(active), siteFacts = state.siteFacts + (pending.serverId to result), sites = state.sites + (pending.serverId to result))
        if (result is ApiResult.Success && state.siteDraft?.id == pending.siteId && state.siteDraft?.serverId == pending.serverId) state = state.copy(siteDraft = null, initialSiteDraft = null)
    }
    fun reloadSiteDraft() {
        val draft = state.siteDraft ?: return
        val current = (state.sites[draft.serverId] as? ApiResult.Success)?.value?.firstOrNull { it.id == draft.id } ?: return
        val refreshed = WebSiteDraft.from(current)
        if (!state.busy && state.pendingSites.none { it.siteId == draft.id && it.serverId == draft.serverId }) state = state.copy(siteDraft = refreshed, initialSiteDraft = refreshed, problemCode = null, uncertain = false)
    }
    fun fileReference(path: String) = work { active ->
        val result = installations.fileReference(active, InstallationService.Nginx, path)
        verify(active)
        referenceResult(result)
    }
    fun upload(uri: Uri) = work { active ->
        val document = uploadDocuments.open(uri.toString())
        if (document == null) { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
        val result = installations.upload(active, InstallationService.Nginx, document) { bytes ->
            // API progress runs on IO; marshal Compose state changes back to the ViewModel dispatcher.
            viewModelScope.launch { if (activeSession === active && state.busy) state = state.copy(uploadBytes = bytes) }
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
            intent = installations.prepare(active, kind, NginxInstallationRequest(true,
                version?.trim()?.takeIf(String::isNotBlank), if (packageSource) reference?.id else null))
        }
        val result = installations.submit(requireNotNull(intent), elevationAnswers)
        verify(active)
        failure(result)
        state = state.copy(pendingInstallation = installations.pending(active).any { it.service == InstallationService.Nginx })
        if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
        if (result is ApiResult.Success) {
            intent = null
            state = state.copy(installation = result.value, installationVerified = true, reference = null, pendingInstallation = false)
            if (!result.value.state.active) load(active)
        }
    }
    val mutationsBlocked get() = state.busy || state.loading || state.uncertain ||
        state.operation?.state?.active == true || state.installation?.state?.active == true ||
        state.pendingInstallation || intent != null
    fun integrate(candidate: WebServerCandidate) {
        if (mutationsBlocked || state.candidates.none { it.id == candidate.id } || state.pending.any { it.target == candidate.id }) return
        webMutation { active -> webServers.integrate(active, candidate, elevationAnswers) }
    }
    fun uninstall(server: WebServer) {
        if (state.selectedId != server.id || state.servers.none { it.id == server.id && it.canUninstall } ||
            state.busy || state.loading || state.operation?.state?.active == true ||
            state.pending.any { it.target == server.id } || state.installation?.state?.active == true ||
            state.pendingInstallation || intent != null) return
        install(null, false, InstallationKind.Uninstall)
    }
    fun lifecycle(server: WebServer, action: WebServerAction) {
        if (mutationsBlocked || state.pending.any { it.target == server.id }) return
        val current = state.servers.firstOrNull { it.id == server.id } ?: return
        if (!action.supported(current)) return
        webMutation { active -> webServers.lifecycle(active, current, action, elevationAnswers) }
    }
    fun resume(pending: PendingWebServerRequest) {
        if (state.busy || state.loading || state.operation?.state?.active == true || state.installation?.state?.active == true || state.pendingInstallation || intent != null) return
        webMutation { active -> webServers.resume(active, pending, elevationAnswers) }
    }
    fun acceptIntegrationFacts(pending: PendingWebServerRequest) = work { active ->
        load(active)
        if (state.uncertain || state.operation?.state?.active == true || state.installation?.state?.active == true ||
            state.pendingInstallation || pending.action != "integrate" || pending.operationId != null ||
            state.servers.none { it.id == pending.target } || state.tests[pending.target]?.valid != true) return@work
        webServers.acceptIntegrationFacts(active, pending)
        state = state.copy(pending = webServers.pending(active))
    }
    fun recover(id: String) = webMutation { active -> webServers.recoverById(active, id.trim()) }
    private fun webMutation(call: suspend (SessionState.Active) -> ApiResult<WebServerOperation>) = work { active ->
        state = state.copy(operationVerified = false)
        val result = call(active); verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(operation = result.value, operationVerified = true)
        state = state.copy(pending = webServers.pending(active))
        if (result is ApiResult.Success && !result.value.state.active) load(active)
    }
    fun pollTasks() = work { active ->
        var completed = false
        var failed: ApiResult<*>? = null
        state.operation?.takeIf { it.state.active }?.let { operation ->
            state = state.copy(operationVerified = false)
            val result = webServers.operation(active, operation.operationId)
            verify(active); failure(result)
            if (result !is ApiResult.Success) failed = result
            if (result is ApiResult.Success) {
                state = state.copy(operation = result.value, operationVerified = true)
                completed = !result.value.state.active
            }
        }
        state.installation?.takeIf { it.state.active }?.let { operation ->
            state = state.copy(installationVerified = false)
            val result = installations.operation(active, operation.operationId)
            verify(active); failure(result)
            if (result !is ApiResult.Success && failed == null) failed = result
            if (result is ApiResult.Success) {
                state = state.copy(installation = result.value, installationVerified = true)
                completed = completed || !result.value.state.active
            }
        }
        if (failed != null) { failure(requireNotNull(failed)); state = state.copy(uncertain = true) }
        else if (completed) load(active)
    }
    fun pollWeb() = webMutation { active -> webServers.operation(active, requireNotNull(state.operation).operationId) }
    fun pollInstallation() = work { active ->
        state = state.copy(installationVerified = false)
        val result = installations.operation(active, requireNotNull(state.installation).operationId)
        verify(active); failure(result)
        if (result is ApiResult.Success) {
            state = state.copy(installation = result.value, installationVerified = true)
            if (!result.value.state.active) load(active)
        }
    }
    fun retryInstallation() { if (intent != null) install(null, false) }
    val intentVersion get() = (intent?.request as? NginxInstallationRequest)?.version
    val intentUsesPackage get() = (intent?.request as? NginxInstallationRequest)?.fileReferenceId != null
    val hasIntent get() = intent != null
    private fun failure(result: ApiResult<*>) {
        state = state.copy(problemCode = (result as? ApiResult.Problem)?.code, uncertain = result is ApiResult.Transport)
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        dismissCancellation()
        val active = activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = NginxState(); sessionEpoch++ }
        if (state.busy || state.loading) {
            NginxDiagnostics.event("work.skipped busy=${state.busy} loading=${state.loading} epoch=$sessionEpoch")
            return
        }
        NginxDiagnostics.event("work.begin epoch=$sessionEpoch")
        workGeneration++
        state = state.copy(busy = true, problemCode = null, uncertain = false, requestVersion = workGeneration)
        viewModelScope.launch {
            try { verify(active); block(active) }
            catch (cancelled: CancellationException) { NginxDiagnostics.event("work.cancelled epoch=$sessionEpoch"); throw cancelled }
            catch (error: Exception) {
                NginxDiagnostics.event("work.exception type=${error.javaClass.simpleName} epoch=$sessionEpoch")
                if (activeSession === active) state = state.copy(uncertain = true, operationVerified = false, installationVerified = false,
                    pendingSites = runCatching { webSites.pending(active) }.getOrDefault(state.pendingSites))
            }
            finally {
                if (activeSession === active) state = state.copy(busy = false, loading = false)
                NginxDiagnostics.event("work.end ownerCurrent=${activeSession === active} busy=${state.busy} loading=${state.loading} uncertain=${state.uncertain} intent=${intent != null} web=${state.operation?.state} install=${state.installation?.state} pendingInstall=${state.pendingInstallation}")
                state.pending.forEach { NginxDiagnostics.event("block.pending-web action=${it.action} target=${it.target} attempted=${it.attempted} operation=${it.operationId}") }
                if (activeSession === active) installations.pending(active).filter { it.service == InstallationService.Nginx }.forEach {
                    NginxDiagnostics.event("block.pending-install kind=${it.kind} attempted=${it.attempted} operation=${it.operationId}")
                }
            }
        }
    }
    private fun verify(active: SessionState.Active) { if (activeSession !== active) throw CancellationException("Nginx session changed") }
    companion object {
        val windowsSystems = setOf(HostOperatingSystemKind.Windows10, HostOperatingSystemKind.Windows11, HostOperatingSystemKind.WindowsServer)

        /**
         * The hosts that install Nginx from their own package manager, so an instance that is already
         * there is taken over instead of a second one being installed beside it (`features/Nginx.md`,
         * 安装来源). The generic Linux mark belongs here: it is what a supported Debian-family host that
         * named no distribution reports.
         */
        val aptSystems = setOf(HostOperatingSystemKind.Ubuntu, HostOperatingSystemKind.Debian, HostOperatingSystemKind.Linux)
    }
}
