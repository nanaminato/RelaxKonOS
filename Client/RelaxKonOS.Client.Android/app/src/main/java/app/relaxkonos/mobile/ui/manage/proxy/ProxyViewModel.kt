package app.relaxkonos.mobile.ui.manage.proxy

import app.relaxkonos.mobile.core.auth.AuthSession
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

internal data class ProxyState(
    val runtime: ManagedRuntimeInstallation? = null, val runtimeSources: List<ManagedRuntimeSource> = emptyList(),
    val runtimeCatalog: ManagedRuntimeCatalog? = null, val runtimePlan: ManagedRuntimeInstallationRequest? = null,
    val runtimeProblems: List<String> = emptyList(), val runtimeDependencies: String? = null, val runtimeRecovery: String? = null,
    val requestVersion: Long = 0, val busy: Boolean = false, val overview: ApiResult<ProxyOverview>? = null,

    val profiles: ApiResult<List<ProxyProfile>>? = null, val subscriptions: ApiResult<List<ProxySubscription>>? = null,
    val groups: ApiResult<List<ProxyGroup>>? = null, val routing: ApiResult<ProxyRoutingMode>? = null,
    val downloadOptions: Boolean = false,
    val delays: Map<String, ApiResult<ProxyDelay>> = emptyMap(), val testingProxies: Set<String> = emptySet(),
    val testingGroup: String? = null, val pending: List<PendingProxyRequest> = emptyList(),
    val operation: ProxyOperation? = null, val operationVerified: Boolean = false,
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false,
    val settings: ApiResult<ProxySettings>? = null, val recovery: ApiResult<ProxyRecovery>? = null,
    val traffic: ApiResult<ProxyTraffic>? = null, val connections: ApiResult<List<ProxyConnection>>? = null,
    val logs: ApiResult<List<ProxyLog>>? = null, val dns: ApiResult<ProxyDns>? = null, val resources: ApiResult<ManagedRuntimeResources>? = null,
    val diagnosticsBusy: Boolean = false, val diagnosticsFailed: Boolean = false, val diagnosticsSection: String? = null, val diagnosticsAtMillis: Long? = null, val problemCode: String? = null, val uncertain: Boolean = false, val savedEpoch: Int = 0, val observedAtMillis: Long? = null)
// Latency samples are independent reads and do not alter the facts reviewed for a mutation.
private fun ProxyState.changeFacts() = copy(delays = emptyMap(), testingProxies = emptySet(), testingGroup = null)
internal class ProxyChangeReview(val owner: SessionState.Active, val version: Long,
    val observed: ProxyState, internal val action: () -> Unit)
internal data class ProxyRecoveryDraft(val id: String, val identified: Boolean = false)
internal class ProxyRecoveryForm(val owner: SessionState.Active, val installation: Boolean, val requestVersion: Long,
    val pending: PendingInstallationRequest?, initialId: String) {
    var draft by mutableStateOf(ProxyRecoveryDraft(initialId)); internal set
}
internal class ProxyInstallationCancellation(val owner: SessionState.Active, val operation: InstallationOperation, val version: Long)
internal class ProxyAdoptionReview(val owner: SessionState.Active, val pending: PendingProxyRequest, val version: Long)
internal class ProxyViewModel(private val session: AuthSession, private val repository: ProxyRepository,
    private val installations: InstallationRepository, private val operationIndex: OperationIndex,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    private var workGeneration = 0L
    private var diagnosticsGeneration = 0L
    private var diagnosticsJob: Job? = null
    private var interactionActive = false
    var interactionVersion by mutableStateOf(0L); private set
    var adoptionReview by mutableStateOf<ProxyAdoptionReview?>(null); private set
    var changeReview by mutableStateOf<ProxyChangeReview?>(null); private set
    fun isCurrentInteraction(version: Long, active: SessionState.Active) =
        interactionActive && interactionVersion == version && activeSession === active && owner === active
    var recoveryForm by mutableStateOf<ProxyRecoveryForm?>(null); private set
    fun requestRecovery(version: Long, active: SessionState.Active, observed: ProxyState, installation: Boolean) {
        if (!isCurrentInteraction(version, active) || state.busy || state.changeFacts() != observed.changeFacts() ||
            ServerCapabilities.PROXY !in active.capabilities || installation && !active.privilegedOperations ||
            recoveryForm != null || changeReview != null || adoptionReview != null || installationCancellation != null) return
        val pending = try { if (installation) installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo } else null }
        catch (_: CancellationException) { return }
        catch (_: Exception) { if (activeSession === active && owner === active) state = state.copy(uncertain = true); return }
        if (!isCurrentInteraction(version, active)) return
        if (installation && state.pendingInstallation != (pending != null)) { state = state.copy(uncertain = true); return }
        stopDiagnostics()
        recoveryForm = ProxyRecoveryForm(active, installation, state.requestVersion, pending,
            if (installation && state.installationVerified) state.installation?.operationId.orEmpty() else "")
    }
    private fun ownsRecovery(form: ProxyRecoveryForm, version: Long) = recoveryForm === form &&
        isCurrentInteraction(version, form.owner) && !state.busy && state.requestVersion == form.requestVersion
    fun changeRecoveryId(form: ProxyRecoveryForm, version: Long, id: String) {
        if (ownsRecovery(form, version) && form.draft.id != id) form.draft = ProxyRecoveryDraft(id)
    }
    fun identifyRecovery(form: ProxyRecoveryForm, version: Long, draft: ProxyRecoveryDraft, identified: Boolean) {
        if (ownsRecovery(form, version) && form.draft === draft && form.pending != null) form.draft = draft.copy(identified = identified)
    }
    fun dismissRecovery(form: ProxyRecoveryForm, version: Long) {
        if (ownsRecovery(form, version)) recoveryForm = null
    }
    fun submitRecovery(form: ProxyRecoveryForm, version: Long, draft: ProxyRecoveryDraft) {
        if (!ownsRecovery(form, version) || form.draft !== draft || form.pending != null && !draft.identified) return
        val id = runCatching { InstallationRoutes.canonicalId(draft.id.trim()) }.getOrNull() ?: return
        recoveryForm = null
        if (form.installation) recoverInstall(form, id) else recoverOperation(id)
    }
    fun requestChange(version: Long, active: SessionState.Active, observed: ProxyState, action: () -> Unit) {
        if (!interactionActive || interactionVersion != version || activeSession !== active || owner !== active ||
            !active.privilegedOperations || ServerCapabilities.PROXY !in active.capabilities || state.busy || state.changeFacts() != observed.changeFacts() ||
            changeReview != null || adoptionReview != null || installationCancellation != null || recoveryForm != null) return
        stopDiagnostics()
        changeReview = ProxyChangeReview(active, version, state.changeFacts(), action)
    }
    fun dismissChange(review: ProxyChangeReview) {
        if (changeReview === review) changeReview = null
    }
    fun submitChange(review: ProxyChangeReview) {
        if (changeReview !== review || !interactionActive || interactionVersion != review.version ||
            activeSession !== review.owner || owner !== review.owner || state.busy || state.changeFacts() != review.observed) return
        changeReview = null
        review.action()
    }
    fun setInteractionActive(active: Boolean) {
        if (!active && interactionActive) interactionVersion++
        interactionActive = active
        if (!active) { adoptionReview = null; installationCancellation = null; changeReview = null }
    }
    fun leavePage() { setInteractionActive(false); recoveryForm = null }
    fun requestAdoption(version: Long, active: SessionState.Active, pending: PendingProxyRequest) {
        if (!interactionActive || interactionVersion != version || activeSession !== active || owner !== active || state.busy || recoveryForm != null || changeReview != null || installationCancellation != null ||
            pending.action != null || state.pending.none { it === pending }) return
        adoptionReview = ProxyAdoptionReview(active, pending, version)
    }
    fun dismissAdoption(review: ProxyAdoptionReview) {
        if (adoptionReview === review && activeSession === review.owner && !state.busy) adoptionReview = null
    }
    var installationCancellation by mutableStateOf<ProxyInstallationCancellation?>(null); private set
    fun dismissInstallationCancellation(review: ProxyInstallationCancellation) {
        if (installationCancellation === review) installationCancellation = null
    }
    fun requestInstallationCancellation(version: Long, active: SessionState.Active, operation: InstallationOperation) {
        if (interactionActive && interactionVersion == version && activeSession === active && owner === active && active.privilegedOperations && !state.busy && recoveryForm == null && changeReview == null && adoptionReview == null &&
            state.installationVerified && state.installation == operation && operation.state.active && operation.cancellable)
            installationCancellation = ProxyInstallationCancellation(active, operation, version)
    }
    private fun canObserveTasks() = recoveryForm == null && changeReview == null && !state.busy && !state.uncertain && state.problemCode == null && installationCancellation == null && adoptionReview == null
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
        if (next !== owner) { recoveryForm = null; changeReview = null; adoptionReview = null; installationCancellation = null; stopDiagnostics(); owner = next as? SessionState.Active; intent = null; state = ProxyState(); sessionEpoch++ }
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
        clearFacts()
        val overview = repository.overview(active); val profiles = repository.profiles(active); val subscriptions = repository.subscriptions(active)
        verify(active)
        val facts = (overview as? ApiResult.Success)?.value
        val groups = if (facts?.controllerReachable == true && facts.supportsGroups) repository.groups(active) else null
        val routing = if (facts?.controllerReachable == true) repository.routing(active) else null
        val options = if (active.privilegedOperations) repository.downloadOptions(active) else null
        val settings = repository.settings(active); val recovery = repository.recovery(active); val resources = repository.resources(active)
        verify(active)
        state = state.copy(overview = overview, profiles = profiles, subscriptions = subscriptions, groups = groups, routing = routing,
            downloadOptions = (options as? ApiResult.Success)?.value == true, settings = settings, recovery = recovery, resources = resources, pending = repository.pending(active),
            pendingInstallation = installations.pending(active).any { it.service == InstallationService.Mihomo }, observedAtMillis = System.currentTimeMillis())
        val failure = listOfNotNull(overview, profiles, subscriptions, groups, routing, options, settings, recovery, resources).firstOrNull { it !is ApiResult.Success }
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
    fun accept(review: ProxyAdoptionReview) {
        if (!interactionActive || interactionVersion != review.version || adoptionReview !== review || activeSession !== review.owner ||
            owner !== review.owner || state.busy || state.pending.none { it === review.pending }) return
        adoptionReview = null
        work { active ->
            clearFacts()
            try {
                val result = repository.acceptFacts(active, review.pending); verify(active); failure(result)
                if (result is ApiResult.Success) load(active)
            } catch (cancelled: CancellationException) {
                if (activeSession === active) state = state.copy(uncertain = true)
                throw cancelled
            }
        }
    }
    private fun clearFacts() {
        state = state.copy(overview = null, profiles = null, subscriptions = null, groups = null, routing = null,
            settings = null, recovery = null, resources = null, observedAtMillis = null)
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
    fun prepareResources() = work { active ->
        val prior = (state.resources as? ApiResult.Success)?.value
        require(prior?.canPrepare == true)
        state = state.copy(resources = null)
        val result = installations.prepareRuntimeResources(active, elevationAnswers); verify(active); failure(result)
        val observation = repository.resources(active); verify(active); failure(observation)
        state = state.copy(resources = observation)
        if (result is ApiResult.Success && observation is ApiResult.Success) {
            if (ManagedRuntimeResourceWire.matchesReport(result.value, observation.value)) state = state.copy(savedEpoch = state.savedEpoch + 1)
            else state = state.copy(problemCode = "runtime.resource_observation_changed")
        }
    }
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
    fun loadRuntime() = work { active ->
        state = state.copy(runtime = null, runtimeCatalog = null, runtimePlan = intent?.request as? ManagedRuntimeInstallationRequest, runtimeProblems = emptyList(), runtimeDependencies = null, runtimeRecovery = null)
        val installation = repository.runtime(active, "GET", ManagedRuntimeRoutes.INSTALLATION); verify(active); failure(installation)
        if (installation is ApiResult.Success) {
            val json = org.json.JSONObject(installation.value)
            state = state.copy(runtime = ManagedRuntimeInstallation(installation.value),
                runtimeDependencies = if (json.isNull("environment")) null else json.getJSONObject("environment").toString())
        }
        val sources = repository.runtime(active, "GET", ManagedRuntimeRoutes.SOURCES); verify(active); failure(sources)
        state = state.copy(runtimeSources = if (sources is ApiResult.Success) ManagedRuntimeWire.sources(sources.value) else emptyList())
        val recovery = repository.runtime(active, "GET", ManagedRuntimeRoutes.RECOVERY); verify(active); failure(recovery)
        if (recovery is ApiResult.Success) state = state.copy(runtimeRecovery = recovery.value)
    }
    fun loadRuntimeCatalog(source: String, refresh: Boolean, prerelease: Boolean) = work { active ->
        state = state.copy(runtimeCatalog = null, runtimePlan = null, runtimeProblems = emptyList())
        require(state.runtimeSources.any { it.id == source })
        val result = repository.runtime(active, if (refresh) "POST" else "GET", ManagedRuntimeRoutes.catalog(source, refresh, prerelease),
            if (refresh) JsonBody() else null, if (refresh) java.util.UUID.randomUUID().toString() else null)
        verify(active); failure(result)
        if (result is ApiResult.Success) { val catalog = ManagedRuntimeWire.catalog(result.value); require(catalog.source == source); state = state.copy(runtimeCatalog = catalog) }
    }
    fun clearRuntimePlan() { if (!state.busy && !hasIntent) state = state.copy(runtimePlan = null, runtimeProblems = emptyList(), runtimeDependencies = null) }
    fun planRuntime(kind: InstallationKind, release: String?, prerelease: Boolean, keepData: Boolean) = work { active ->
        require(!hasIntent && !state.pendingInstallation)
        state = state.copy(runtimePlan = null, runtimeProblems = emptyList(), runtimeDependencies = null)
        val installation = requireNotNull(state.runtime)
        val catalog = state.runtimeCatalog
        if (kind != InstallationKind.Uninstall) {
            require(catalog?.fresh == true && catalog.problem == null)
            require(release != null && installation.eligible(kind, release))
            require(catalog.releases.any { it.id == release && it.trust == "confirmed" && it.supported && (prerelease || it.channel == "stable") })
        }
        val key = java.util.UUID.randomUUID().toString()
        val body = JsonBody().raw("sourceId", if (kind == InstallationKind.Uninstall) "null" else org.json.JSONObject.quote(requireNotNull(catalog).source))
            .objectField("request", installation.request(kind, catalog, release, key, prerelease, keepData))
        val result = repository.runtime(active, "POST", ManagedRuntimeRoutes.PLAN, body, key); verify(active); failure(result)
        if (result is ApiResult.Success) {
            val json = org.json.JSONObject(result.value); val problems = json.getJSONArray("problems")
            state = state.copy(runtimeDependencies = if (json.isNull("dependencies")) null else json.getJSONObject("dependencies").toString())
            state = state.copy(runtimeProblems = (0 until problems.length()).map { problems.getString(it) })
            if (json.getBoolean("allowed") && problems.length() == 0 && !json.isNull("plan")) {
                val request = ManagedRuntimeInstallationRequest(true, json.getJSONObject("plan").toString())
                require(request.key == key && request.kind == kind)
                val expected = org.json.JSONObject(body.toByteArray().decodeToString()).getJSONObject("request")
                val actual = json.getJSONObject("plan").getJSONObject("request")
                require(expected.keys().asSequence().toSet() == actual.keys().asSequence().toSet())
                require(expected.keys().asSequence().all { expected.get(it).toString() == actual.get(it).toString() })
                state = state.copy(runtimePlan = request)
            }
        }
    }
    fun installRuntime() = work { active ->
        if (intent == null) {
            val request = requireNotNull(state.runtimePlan)
            intent = installations.prepare(active, request.kind, request)
        }
        val result = installations.submit(requireNotNull(intent), elevationAnswers); verify(active); failure(result)
        state = state.copy(pendingInstallation = installations.pending(active).any { it.service == InstallationService.Mihomo })
        if (result is ApiResult.Success) { intent = null; state = state.copy(runtimePlan = null, pendingInstallation = false); installationResult(active, result) }
        else if (result is ApiResult.Problem && !state.pendingInstallation) intent = null
    }
    fun retryInstall() { if (intent != null) installRuntime() }
    fun recoverRuntime(action: String, operationId: String, confirmed: Boolean) = work { active ->
        require(action in setOf("retry", "stop", "close"))
        require(action != "close" || confirmed)
        val observedRecovery = org.json.JSONObject(requireNotNull(state.runtimeRecovery))
        require(observedRecovery.getBoolean("required") && observedRecovery.getString("operationId") == operationId)
        if (action == "close") {
            val service = observedRecovery.getJSONObject("service")
            require(!observedRecovery.isNull("recoveryProblemCode") && !service.getBoolean("running") && observedRecovery.isNull("serviceProblem"))
        }
        val body = JsonBody().string("operationId", InstallationRoutes.canonicalId(operationId)).bool("confirmed", action == "close")
        val result = installations.runtimeRecovery(active, action, body, elevationAnswers)
        verify(active); failure(result)
        state = state.copy(runtime = null, runtimePlan = null, runtimeRecovery = null)
        val recovery = repository.runtime(active, "GET", ManagedRuntimeRoutes.RECOVERY); verify(active); failure(recovery)
        if (recovery is ApiResult.Success) state = state.copy(runtimeRecovery = recovery.value)
    }
    fun acceptInstallationFacts() = work { active ->
        val pending = installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo } ?: return@work
        val overview = repository.overview(active); verify(active); failure(overview)
        if (overview !is ApiResult.Success) return@work
        val result = installations.acceptCurrentFacts(active, pending); verify(active); failure(result)
        if (result is ApiResult.Success) { intent = null; load(active) }
    }
    val hasIntent get() = intent != null
    private fun recoverInstall(form: ProxyRecoveryForm, id: String) = work { active ->
        if (active !== form.owner) return@work
        val pending = installations.pending(active).firstOrNull { it.service == InstallationService.Mihomo }
        if (pending != form.pending) { state = state.copy(uncertain = true); return@work }
        state = state.copy(installationVerified = false)
        val result = if (pending == null) installations.recoverById(active, id.trim(), InstallationService.Mihomo, InstallationKind.entries.toSet()) else installations.identifyOriginal(active, pending, id.trim(), InstallationKind.entries.toSet())
        installationResult(active, result)
        if (result is ApiResult.Success && result.value.service == InstallationService.Mihomo) intent = null
    }
    fun pollInstall() = work { active ->
        val id = state.installation?.operationId ?: return@work
        state = state.copy(installationVerified = false)
        installationResult(active, installations.operation(active, id))
    }
    fun cancelInstall(review: ProxyInstallationCancellation) {
        if (!interactionActive || interactionVersion != review.version || installationCancellation !== review) return
        installationCancellation = null
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
        recoveryForm = null
        changeReview = null
        adoptionReview = null
        installationCancellation = null
        val active = activeSession ?: return
        if (owner !== active) { owner = active; intent = null; state = ProxyState(); sessionEpoch++ }
        if (state.busy) return
        stopDiagnostics()
        workGeneration++
        state = state.copy(busy = true, problemCode = null, uncertain = false, requestVersion = workGeneration)
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
