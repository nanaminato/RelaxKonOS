package app.relaxkonos.mobile.ui.manage.proxy

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

internal data class ProxyState(val busy: Boolean = false, val overview: ApiResult<ProxyOverview>? = null,
    val profiles: ApiResult<List<ProxyProfile>>? = null, val subscriptions: ApiResult<List<ProxySubscription>>? = null,
    val groups: ApiResult<List<ProxyGroup>>? = null, val routing: ApiResult<ProxyRoutingMode>? = null,
    val downloadOptions: Boolean = false, val download: ApiResult<ProxyDownload>? = null,
    val delay: ApiResult<ProxyDelay>? = null, val pending: List<PendingProxyRequest> = emptyList(),
    val operation: ProxyOperation? = null, val operationVerified: Boolean = false,
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false,
    val reference: InstallationFileReference? = null, val uploadBytes: Long? = null,
    val settings: ApiResult<ProxySettings>? = null, val recovery: ApiResult<ProxyRecovery>? = null,
    val traffic: ApiResult<ProxyTraffic>? = null, val connections: ApiResult<List<ProxyConnection>>? = null,
    val logs: ApiResult<List<ProxyLog>>? = null, val dns: ApiResult<ProxyDns>? = null, val geoData: ApiResult<ProxyGeoData>? = null,
    val diagnosticsAtMillis: Long? = null, val problemCode: String? = null, val uncertain: Boolean = false, val savedEpoch: Int = 0, val observedAtMillis: Long? = null)
internal class ProxyViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var owner: SessionState.Active? = null
    private var intent: InstallationSubmission? = null
    var state by mutableStateOf(ProxyState())
        private set
    var sessionEpoch by mutableIntStateOf(0)
        private set
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== owner) { owner = next as? SessionState.Active; intent = null; state = ProxyState(); sessionEpoch++ }
    } } }
    fun refresh() = work { active ->
        load(active)
        val id = container.operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Proxy }?.operationId ?: state.operation?.operationId
        if (id != null) operationResult(active, container.proxy.operation(active, id))
        if (active.privilegedOperations) {
            val result = container.installations.active(active, InstallationService.Mihomo); verify(active)
            if (result is ApiResult.Success && result.value != null) installationResult(active, ApiResult.Success(result.value))
            else if (result !is ApiResult.Success) state = state.copy(installationVerified = false)
            else {
                val old = state.installation?.operationId ?: container.operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Mihomo.name }?.operationId
                if (old != null) installationResult(active, container.installations.operation(active, old))
            }
        }
    }
    private suspend fun load(active: SessionState.Active) {
        val overview = container.proxy.overview(active); val profiles = container.proxy.profiles(active); val subscriptions = container.proxy.subscriptions(active)
        verify(active)
        val facts = (overview as? ApiResult.Success)?.value
        val groups = if (facts?.controllerReachable == true && facts.supportsGroups) container.proxy.groups(active) else null
        val routing = if (facts?.controllerReachable == true) container.proxy.routing(active) else null
        val options = if (active.privilegedOperations) container.proxy.downloadOptions(active) else null
        val settings = container.proxy.settings(active); val recovery = container.proxy.recovery(active); val geoData = container.proxy.geoData(active)
        verify(active)
        state = state.copy(overview = overview, profiles = profiles, subscriptions = subscriptions, groups = groups, routing = routing,
            downloadOptions = (options as? ApiResult.Success)?.value == true, settings = settings, recovery = recovery, geoData = geoData, pending = container.proxy.pending(active),
            pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Mihomo }, observedAtMillis = System.currentTimeMillis())
        val failure = listOfNotNull(overview, profiles, subscriptions, groups, routing, options, settings, recovery, geoData).firstOrNull { it !is ApiResult.Success }
        if (failure != null) failure(failure)
    }
    fun queue(action: ProxyAction, target: String? = null) = work { active ->
        operationResult(active, container.proxy.queue(active, action, target)); retainAccepted(active); load(active)
    }
    fun resume(pending: PendingProxyRequest) = work { active -> operationResult(active, container.proxy.resume(active, pending)); retainAccepted(active); load(active) }
    private fun retainAccepted(active: SessionState.Active) {
        verify(active)
        val reference = container.operationIndex.forOwner(active).firstOrNull { it.domain == OperationDomain.Proxy } ?: return
        if (reference.operationId != state.operation?.operationId) state = state.copy(
            operation = ProxyOperation(reference.operationId, reference.resourceId, ProxyOperationState.Queued, "queued", ""), operationVerified = false)
    }
    fun accept(pending: PendingProxyRequest) = work { active ->
        val result = container.proxy.acceptFacts(active, pending); verify(active); failure(result); load(active)
    }
    fun recoverOperation(id: String) = work { active -> operationResult(active, container.proxy.operation(active, id.trim())) }
    fun pollOperation() = work { active ->
        val id = state.operation?.operationId ?: return@work
        val result = container.proxy.operation(active, id); operationResult(active, result)
        if (result is ApiResult.Success && !result.value.state.active) load(active)
    }
    private fun operationResult(active: SessionState.Active, result: ApiResult<ProxyOperation>) {
        verify(active); failure(result)
        state = state.copy(operationVerified = result is ApiResult.Success, operation = (result as? ApiResult.Success)?.value ?: state.operation)
        if (result is ApiResult.Success && result.value.problemCode.isNotBlank()) state = state.copy(problemCode = result.value.problemCode)
    }
    fun saveProfile(profile: ProxyProfile?, name: String) = mutation { active -> container.proxy.saveProfile(active, profile?.id, ProxyProfileRequest(name, profile?.revision)) }
    fun activate(profile: ProxyProfile) = mutation { active -> container.proxy.activateProfile(active, profile.id) }
    fun delete(profile: ProxyProfile) = mutation { active -> container.proxy.deleteProfile(active, profile.id) }
    fun apply(profile: ProxyProfile, yaml: String) = mutation { active -> container.proxy.apply(active, profile.id, yaml) }
    fun import(url: String, name: String, route: ProxyDownloadRoute) = mutation { active -> container.proxy.import(active, ProxyImportRequest(url.trim(), name, route)) }
    fun select(group: ProxyGroup, proxy: String) = mutation { active -> container.proxy.select(active, group, proxy) }
    fun routing(mode: ProxyRoutingMode) = mutation { active -> container.proxy.routing(active, mode) }
    fun saveSettings(settings: ProxySettings) = mutation { active -> container.proxy.saveSettings(active, settings) }
    fun configureGeoData(path: String) = mutation { active -> container.proxy.configureGeoData(active, path) }
    fun closeConnection(id: String) = mutation { active -> container.proxy.closeConnection(active, id) }
    fun diagnostics() = work { active ->
        val overview = (state.overview as? ApiResult.Success)?.value ?: return@work
        val traffic = container.proxy.traffic(active)
        val connections = if (overview.supportsConnections) container.proxy.connections(active) else null
        val logs = if (overview.supportsLogs) container.proxy.logs(active) else null
        val dns = if (overview.supportsDns) container.proxy.dns(active) else null; verify(active)
        state = state.copy(traffic = traffic, connections = connections, logs = logs, dns = dns, diagnosticsAtMillis = System.currentTimeMillis())
        listOfNotNull(traffic, connections, logs, dns).forEach { failure(it) }
    }
    fun delay(group: String, proxy: String, url: String) = work { active ->
        val result = container.proxy.delay(active, group, proxy, url.trim(), 5000); verify(active); failure(result); state = state.copy(delay = result)
    }
    private fun mutation(call: suspend (SessionState.Active) -> ApiResult<*>) = work { active ->
        val result = call(active); verify(active); failure(result)
        if (result is ApiResult.Success) state = state.copy(savedEpoch = state.savedEpoch + 1)
        load(active)
    }
    fun download(version: String) = work { active ->
        val result = container.proxy.download(active, version); verify(active); failure(result); state = state.copy(download = result)
    }
    fun clearReference() { if (!state.busy && intent == null) state = state.copy(reference = null, download = null) }
    fun reference(path: String) = work { active ->
        val result = container.installations.fileReference(active, InstallationService.Mihomo, path); verify(active); failure(result)
        state = state.copy(reference = (result as? ApiResult.Success)?.value)
    }
    fun upload(uri: Uri) = work { active ->
        val document = container.uploadDocuments.open(uri.toString()) ?: run { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
        val result = container.installations.upload(active, InstallationService.Mihomo, document) { bytes ->
            viewModelScope.launch { if (container.activeSession === active && state.busy) state = state.copy(uploadBytes = bytes) }
        }; verify(active); failure(result); state = state.copy(reference = (result as? ApiResult.Success)?.value, uploadBytes = null)
    }
    fun install(kind: InstallationKind, version: String?, rollback: Boolean, packageSource: Boolean) = work { active ->
        if (intent == null) {
            if (packageSource && state.reference?.expired() != false) { state = state.copy(problemCode = InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE); return@work }
            intent = container.installations.prepare(active, kind, MihomoInstallationRequest(true, version?.trim()?.takeIf(String::isNotEmpty), rollback, if (packageSource) state.reference?.id else null))
        }
        val result = container.installations.submit(requireNotNull(intent), container.elevationAnswers); verify(active); failure(result)
        state = state.copy(pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Mihomo })
        if (result is ApiResult.Success) { intent = null; installationResult(active, result); state = state.copy(reference = null, pendingInstallation = false) }
        else if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun retryInstall() { val old = intent ?: return; val r = old.request as MihomoInstallationRequest; install(old.kind, r.version, r.rollback, r.fileReferenceId != null) }
    val hasIntent get() = intent != null
    val currentIntent get() = intent
    fun recoverInstall(id: String, identified: Boolean) = work { active ->
        val pending = container.installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo }
        if (pending != null && !identified) return@work
        val result = if (pending == null) container.installations.recoverById(active, id.trim()) else container.installations.identifyOriginal(active, pending, id.trim())
        installationResult(active, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Mihomo) intent = null
    }
    fun pollInstall() = work { active -> val id = state.installation?.operationId ?: return@work; installationResult(active, container.installations.operation(active, id)) }
    fun cancelInstall() = work { active -> val id = state.installation?.operationId ?: return@work; installationResult(active, container.installations.cancel(active, id)) }
    private suspend fun installationResult(active: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(active); failure(result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Mihomo) {
            state = state.copy(installation = result.value, installationVerified = true, pendingInstallation = container.installations.pending(active).any { it.service == InstallationService.Mihomo })
            if (!result.value.state.active) load(active)
        } else state = state.copy(installationVerified = false, uncertain = true)
    }
    private fun failure(result: ApiResult<*>) {
        if (result is ApiResult.Problem) state = state.copy(problemCode = result.code)
        if (result is ApiResult.Transport) state = state.copy(uncertain = true)
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = container.activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = ProxyState(); sessionEpoch++ }
        if (state.busy) return
        state = state.copy(busy = true, problemCode = null, uncertain = false)
        viewModelScope.launch {
            try { block(active) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (container.activeSession === active) state = state.copy(uncertain = true, operationVerified = false, installationVerified = false) }
            finally { if (container.activeSession === active) state = state.copy(busy = false,
                pending = runCatching { container.proxy.pending(active) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { container.installations.pending(active).any { it.service == InstallationService.Mihomo } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun verify(active: SessionState.Active) { if (container.activeSession !== active) throw CancellationException("Proxy session changed") }
}
