package app.relaxkonos.mobile.ui.manage.proxy

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
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.Job

internal data class ProxyState(val busy: Boolean = false, val overview: ApiResult<ProxyOverview>? = null,
    val profiles: ApiResult<List<ProxyProfile>>? = null, val subscriptions: ApiResult<List<ProxySubscription>>? = null,
    val groups: ApiResult<List<ProxyGroup>>? = null, val routing: ApiResult<ProxyRoutingMode>? = null,
    val releases: ApiResult<List<ProxyRelease>>? = null,
    val downloadOptions: Boolean = false, val download: ApiResult<ProxyDownload>? = null,
    val delays: Map<String, ApiResult<ProxyDelay>> = emptyMap(), val testingProxies: Set<String> = emptySet(),
    val testingGroup: String? = null, val pending: List<PendingProxyRequest> = emptyList(),
    val operation: ProxyOperation? = null, val operationVerified: Boolean = false,
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false,
    val reference: InstallationFileReference? = null, val uploadBytes: Long? = null,
    val settings: ApiResult<ProxySettings>? = null, val recovery: ApiResult<ProxyRecovery>? = null,
    val traffic: ApiResult<ProxyTraffic>? = null, val connections: ApiResult<List<ProxyConnection>>? = null,
    val logs: ApiResult<List<ProxyLog>>? = null, val dns: ApiResult<ProxyDns>? = null, val geoData: ApiResult<ProxyGeoData>? = null,
    val diagnosticsBusy: Boolean = false, val diagnosticsFailed: Boolean = false, val diagnosticsSection: String? = null, val diagnosticsAtMillis: Long? = null, val problemCode: String? = null, val uncertain: Boolean = false, val savedEpoch: Int = 0, val observedAtMillis: Long? = null)
internal class ProxyInstallationCancellation(val owner: SessionState.Active, val operation: InstallationOperation)
internal class ProxyViewModel(private val session: AuthSession, private val repository: ProxyRepository,
    private val installations: InstallationRepository, private val operationIndex: OperationIndex,
    private val elevationAnswers: ElevationAnswerProvider, private val uploadDocuments: UploadDocumentOpener) : ViewModel() {
    private var workGeneration = 0L
    private var diagnosticsGeneration = 0L
    private var diagnosticsJob: Job? = null
    var installationCancellation by mutableStateOf<ProxyInstallationCancellation?>(null); private set
    fun dismissInstallationCancellation() { installationCancellation = null }
    fun requestInstallationCancellation(active: SessionState.Active, operation: InstallationOperation) {
        if (activeSession === active && owner === active && active.privilegedOperations && !state.busy &&
            state.installationVerified && state.installation == operation && operation.state.active && operation.cancellable)
            installationCancellation = ProxyInstallationCancellation(active, operation)
    }
    private fun canObserveTasks() = !state.busy && !state.uncertain && state.problemCode == null && installationCancellation == null
    suspend fun observeTasks(canObserve: () -> Boolean) {
        val active = activeSession ?: return
        val operation = state.operation?.takeIf { state.operationVerified && it.state.active }
        val installation = state.installation?.takeIf { state.installationVerified && it.state.active }
        val generation = workGeneration
        if ((operation != null || installation != null) && canObserveTasks() && canObserve()) {
            kotlinx.coroutines.delay(1500)
            if (activeSession === active && owner === active && generation == workGeneration &&
                (operation == null || state.operationVerified && state.operation == operation) &&
                (installation == null || state.installationVerified && state.installation == installation) && canObserveTasks() && canObserve()) {
                work { current ->
                    state = state.copy(operationVerified = if (operation != null) false else state.operationVerified,
                        installationVerified = if (installation != null) false else state.installationVerified)
                    val operationResponse = operation?.let { repository.operation(current, it.operationId) }
                    if (operationResponse != null) operationResult(current, operationResponse)
                    val installationResponse = installation?.let { installations.operation(current, it.operationId) }
                    if (installationResponse != null) installationResult(current, installationResponse)
                    if (operationResponse is ApiResult.Success && !operationResponse.value.state.active &&
                        !(installationResponse is ApiResult.Success && !installationResponse.value.state.active)) load(current)
                }
            }
        }
    }

    suspend fun observeDiagnostics(section: String, canObserve: () -> Boolean) {
        val active = activeSession ?: return
        if (section !in setOf("connections", "logs", "settings")) return
        while (activeSession === active && owner === active && canObserveTasks() && canObserve() &&
            (state.overview as? ApiResult.Success)?.value?.controllerReachable == true) {
            diagnostics(section)
            kotlinx.coroutines.delay(3000)
        }
    }

    private val activeSession get() = session.state.value as? SessionState.Active
    private var owner: SessionState.Active? = null
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(ProxyState())
        private set
    var sessionEpoch by mutableIntStateOf(0)
        private set
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== owner) { dismissInstallationCancellation(); stopDiagnostics(); owner = next as? SessionState.Active; intent = null; state = ProxyState(); sessionEpoch++ }
    } } }
    fun refresh() = work { active ->
        state = state.copy(operationVerified = false, installationVerified = false)
        load(active)
        val id = operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Proxy }?.operationId ?: state.operation?.operationId
        if (id != null) operationResult(active, repository.operation(active, id))
        if (active.privilegedOperations) {
            val result = installations.active(active, InstallationService.Mihomo); verify(active)
            if (result is ApiResult.Success && result.value != null) installationResult(active, ApiResult.Success(result.value))
            else if (result !is ApiResult.Success) { failure(result); state = state.copy(installationVerified = false) }
            else {
                val old = state.installation?.operationId ?: operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Mihomo.name }?.operationId
                if (old != null) installationResult(active, installations.operation(active, old))
            }
        }
    }
    private suspend fun load(active: SessionState.Active) {
        state = state.copy(overview = null, profiles = null, subscriptions = null, groups = null, routing = null,
            settings = null, recovery = null, geoData = null, observedAtMillis = null)
        val overview = repository.overview(active); val profiles = repository.profiles(active); val subscriptions = repository.subscriptions(active)
        verify(active)
        val facts = (overview as? ApiResult.Success)?.value
        val groups = if (facts?.controllerReachable == true && facts.supportsGroups) repository.groups(active) else null
        val routing = if (facts?.controllerReachable == true) repository.routing(active) else null
        val options = if (active.privilegedOperations) repository.downloadOptions(active) else null
        val settings = repository.settings(active); val recovery = repository.recovery(active); val geoData = repository.geoData(active)
        verify(active)
        state = state.copy(overview = overview, profiles = profiles, subscriptions = subscriptions, groups = groups, routing = routing,
            downloadOptions = (options as? ApiResult.Success)?.value == true, settings = settings, recovery = recovery, geoData = geoData, pending = repository.pending(active),
            pendingInstallation = installations.pending(active).any { it.service == InstallationService.Mihomo }, observedAtMillis = System.currentTimeMillis())
        val failure = listOfNotNull(overview, profiles, subscriptions, groups, routing, options, settings, recovery, geoData).firstOrNull { it !is ApiResult.Success }
        if (failure != null) failure(failure)
    }
    fun queue(action: ProxyAction, target: String? = null) = work { active ->
        operationResult(active, repository.queue(active, action, target)); retainAccepted(active); load(active)
    }
    fun resume(pending: PendingProxyRequest) = work { active -> operationResult(active, repository.resume(active, pending)); retainAccepted(active); load(active) }
    private fun retainAccepted(active: SessionState.Active) {
        verify(active)
        val reference = operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Proxy } ?: return
        if (reference.operationId != state.operation?.operationId) state = state.copy(
            operation = ProxyOperation(reference.operationId, reference.resourceId, ProxyOperationState.Queued, "queued", ""), operationVerified = false)
    }
    fun accept(pending: PendingProxyRequest) = work { active ->
        val result = repository.acceptFacts(active, pending); verify(active); failure(result); load(active)
    }
    fun recoverOperation(id: String) = work { active ->
        state = state.copy(operationVerified = false)
        operationResult(active, repository.operation(active, id.trim()))
    }
    fun pollOperation() = work { active ->
        val id = state.operation?.operationId ?: return@work
        state = state.copy(operationVerified = false)
        val result = repository.operation(active, id); operationResult(active, result)
        if (result is ApiResult.Success && !result.value.state.active) load(active)
    }
    private fun operationResult(active: SessionState.Active, result: ApiResult<ProxyOperation>) {
        verify(active); failure(result)
        state = state.copy(operationVerified = result is ApiResult.Success, operation = (result as? ApiResult.Success)?.value ?: state.operation)
        if (result is ApiResult.Success && result.value.problemCode.isNotBlank()) state = state.copy(problemCode = result.value.problemCode)
    }
    fun saveProfile(profile: ProxyProfile?, name: String) = mutation { active -> repository.saveProfile(active, profile?.id, ProxyProfileRequest(name.trim(), profile?.revision)) }
    fun activate(profile: ProxyProfile) = mutation { active -> repository.activateProfile(active, profile.id) }
    fun delete(profile: ProxyProfile) = mutation { active -> repository.deleteProfile(active, profile.id) }
    fun apply(profile: ProxyProfile, yaml: String) = mutation { active -> repository.apply(active, profile.id, yaml) }
    fun import(url: String, name: String, route: ProxyDownloadRoute) = mutation { active -> repository.import(active, ProxyImportRequest(url.trim(), name.trim(), route)) }
    fun select(group: ProxyGroup, proxy: String) = mutation { active -> repository.select(active, group, proxy) }
    fun routing(mode: ProxyRoutingMode) = mutation { active -> repository.routing(active, mode) }
    fun saveSettings(settings: ProxySettings) = mutation { active -> repository.saveSettings(active, settings) }
    fun configureGeoData(path: String) = mutation { active -> repository.configureGeoData(active, path) }
    fun closeConnection(id: String) = work { active ->
        val result = repository.closeConnection(active, id); verify(active); failure(result)
        if (result is ApiResult.Success) {
            val current = (state.connections as? ApiResult.Success)?.value
            state = state.copy(connections = current?.let { ApiResult.Success(it.filterNot { connection -> connection.id == id }) })
        }
    }
    fun diagnostics(section: String) {
        val active = activeSession ?: return
        val overview = (state.overview as? ApiResult.Success)?.value ?: return
        if (owner !== active || state.busy || state.diagnosticsBusy || !overview.controllerReachable || section !in setOf("connections", "logs", "settings")) return
        val generation = ++diagnosticsGeneration
        fun verifyRead() { verify(active); if (diagnosticsGeneration != generation) throw CancellationException("Proxy diagnostics changed") }
        state = state.copy(diagnosticsBusy = true, diagnosticsFailed = false, diagnosticsAtMillis = null,
            traffic = if (section == "connections") null else state.traffic)
        diagnosticsJob = viewModelScope.launch {
            try {
                coroutineScope {
                    when (section) {
                        "connections" -> {
                            val traffic = async { repository.traffic(active) }
                            val connections = async { if (overview.supportsConnections) repository.connections(active) else null }
                            val rows = connections.await(); verifyRead()
                            // Render connections before waiting for the flow counters.
                            state = state.copy(connections = rows)
                            val counters = traffic.await(); verifyRead()
                            state = state.copy(traffic = counters, diagnosticsFailed = rows != null && rows !is ApiResult.Success || counters !is ApiResult.Success)
                        }
                        "logs" -> {
                            val logs = if (overview.supportsLogs) repository.logs(active) else null; verifyRead()
                            val previous = (state.logs as? ApiResult.Success)?.value.orEmpty()
                            state = state.copy(diagnosticsFailed = logs != null && logs !is ApiResult.Success, logs = if (logs is ApiResult.Success)
                                ApiResult.Success((logs.value + previous).distinct().sortedByDescending { it.timestampMillis }.take(500)) else logs)
                        }
                        "settings" -> {
                            val dns = if (overview.supportsDns) repository.dns(active) else null; verifyRead()
                            state = state.copy(dns = dns, diagnosticsFailed = dns != null && dns !is ApiResult.Success)
                        }
                    }
                }
                verifyRead()
                state = state.copy(diagnosticsSection = section, diagnosticsAtMillis = System.currentTimeMillis())
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                if (activeSession === active && diagnosticsGeneration == generation) {
                    state = state.copy(diagnosticsFailed = true)
                    when (section) {
                    "connections" -> state = state.copy(connections = ApiResult.Transport(null))
                    "logs" -> state = state.copy(logs = ApiResult.Transport(null))
                    "settings" -> state = state.copy(dns = ApiResult.Transport(null))
                    }
                }
            } finally {
                if (activeSession === active && diagnosticsGeneration == generation) state = state.copy(diagnosticsBusy = false)
            }
        }
    }
    fun stopDiagnostics() {
        diagnosticsGeneration++
        diagnosticsJob?.cancel(); diagnosticsJob = null
        state = state.copy(diagnosticsBusy = false)
    }
    fun testGroup(group: ProxyGroup) {
        val active = activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = ProxyState(); sessionEpoch++ }
        if (state.busy || state.testingGroup != null) return
        val names = group.proxies.toSet()
        state = state.copy(testingGroup = group.name, testingProxies = names, delays = state.delays - names)
        viewModelScope.launch {
            try {
                testProxyGroupLatency(group,
                    test = { proxy -> repository.delay(active, group.name, proxy, "https://www.gstatic.com/generate_204", 5000) },
                    onResult = { proxy, result ->
                        verify(active)
                        state = state.copy(delays = state.delays + (proxy to result), testingProxies = state.testingProxies - proxy)
                    })
            } finally {
                if (activeSession === active) state = state.copy(testingGroup = null, testingProxies = emptySet())
            }
        }
    }
    private fun mutation(call: suspend (SessionState.Active) -> ApiResult<*>) = work { active ->
        val result = call(active); verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(savedEpoch = state.savedEpoch + 1)
        load(active)
        if (result !is ApiResult.Success) failure(result)
    }
    fun loadReleases() = work { active ->
        val result = repository.releases(active); verify(active); failure(result); state = state.copy(releases = result)
    }
    fun download(version: String) = work { active ->
        val result = repository.download(active, version); verify(active); failure(result); state = state.copy(download = result)
    }
    fun clearReference() { if (!state.busy && intent == null) state = state.copy(reference = null, download = null) }
    fun reference(path: String) = work { active ->
        val result = installations.fileReference(active, InstallationService.Mihomo, path); verify(active); failure(result)
        state = state.copy(reference = (result as? ApiResult.Success)?.value)
    }
    fun upload(uri: Uri) = work { active ->
        val document = uploadDocuments.open(uri.toString()) ?: run { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
        val result = installations.upload(active, InstallationService.Mihomo, document) { bytes ->
            viewModelScope.launch { if (activeSession === active && state.busy) state = state.copy(uploadBytes = bytes) }
        }; verify(active); failure(result); state = state.copy(reference = (result as? ApiResult.Success)?.value, uploadBytes = null)
    }
    fun install(kind: InstallationKind, version: String?, rollback: Boolean, packageSource: Boolean) = work { active ->
        if (intent == null) {
            if (packageSource && state.reference?.expired() != false) { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
            intent = installations.prepare(active, kind, MihomoInstallationRequest(true, version?.trim()?.takeIf(String::isNotEmpty), rollback, if (packageSource) state.reference?.id else null))
        }
        val result = installations.submit(requireNotNull(intent), elevationAnswers); verify(active); failure(result)
        state = state.copy(pendingInstallation = installations.pending(active).any { it.service == InstallationService.Mihomo })
        if (result is ApiResult.Success) { intent = null; installationResult(active, result); state = state.copy(reference = null, pendingInstallation = false) }
        else if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun retryInstall() { val old = intent ?: return; val r = old.request as MihomoInstallationRequest; install(old.kind, r.version, r.rollback, r.fileReferenceId != null) }
    fun acceptInstallationFacts() = work { active ->
        val pending = installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo } ?: return@work
        val overview = repository.overview(active); verify(active); failure(overview)
        if (overview !is ApiResult.Success) return@work
        val result = installations.acceptCurrentFacts(active, pending); verify(active); failure(result)
        if (result is ApiResult.Success) { intent = null; load(active) }
    }
    val hasIntent get() = intent != null
    val currentIntent get() = intent
    fun recoverInstall(id: String, identified: Boolean) = work { active ->
        val pending = installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo }
        if (pending != null && !identified) return@work
        state = state.copy(installationVerified = false)
        val result = if (pending == null) installations.recoverById(active, id.trim()) else installations.identifyOriginal(active, pending, id.trim())
        installationResult(active, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Mihomo) intent = null
    }
    fun pollInstall() = work { active ->
        val id = state.installation?.operationId ?: return@work
        state = state.copy(installationVerified = false)
        installationResult(active, installations.operation(active, id))
    }
    fun cancelInstall(review: ProxyInstallationCancellation) {
        if (installationCancellation !== review) return
        dismissInstallationCancellation()
        if (activeSession !== review.owner || owner !== review.owner || !state.installationVerified || state.installation != review.operation) return
        work { active ->
            state = state.copy(installationVerified = false)
            installationResult(active, installations.cancel(active, review.operation.operationId))
        }
    }
    private suspend fun installationResult(active: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(active); failure(result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Mihomo) {
            state = state.copy(installation = result.value, installationVerified = true, pendingInstallation = installations.pending(active).any { it.service == InstallationService.Mihomo })
            if (!result.value.state.active) load(active)
        } else state = state.copy(installationVerified = false, uncertain = true)
    }
    private fun failure(result: ApiResult<*>) {
        if (result is ApiResult.Problem) state = state.copy(problemCode = result.code)
        if (result is ApiResult.Transport) state = state.copy(uncertain = true)
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        dismissInstallationCancellation()
        val active = activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = ProxyState(); sessionEpoch++ }
        if (state.busy) return
        stopDiagnostics()
        workGeneration++
        state = state.copy(busy = true, problemCode = null, uncertain = false)
        viewModelScope.launch {
            try { verify(active); block(active) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (activeSession === active) state = state.copy(uncertain = true, operationVerified = false, installationVerified = false) }
            finally { if (activeSession === active) state = state.copy(busy = false,
                pending = runCatching { repository.pending(active) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { installations.pending(active).any { it.service == InstallationService.Mihomo } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun verify(active: SessionState.Active) { if (activeSession !== active) throw CancellationException("Proxy session changed") }
}
