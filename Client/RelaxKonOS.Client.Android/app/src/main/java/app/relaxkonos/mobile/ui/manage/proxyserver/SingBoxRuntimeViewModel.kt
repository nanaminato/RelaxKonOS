package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*
import org.json.JSONObject
import java.util.UUID

internal class SingBoxRuntimeViewModel(private val session: AuthSession, private val repository: ProxyServerRepository,
    private val installations: InstallationRepository, private val answers: ElevationAnswerProvider) : ViewModel() {
    var owner by mutableStateOf<SessionState.Active?>(null); private set
    var busy by mutableStateOf(false); private set
    var installation by mutableStateOf<ManagedRuntimeInstallation?>(null); private set
    var sources by mutableStateOf(emptyList<ManagedRuntimeSource>()); private set
    var catalog by mutableStateOf<ManagedRuntimeCatalog?>(null); private set
    var plan by mutableStateOf<ManagedRuntimeInstallationRequest?>(null); private set
    var recovery by mutableStateOf<String?>(null); private set
    var dependencies by mutableStateOf<String?>(null); private set
    var problems by mutableStateOf(emptyList<String>()); private set
    var operation by mutableStateOf<InstallationOperation?>(null); private set
    var operationVerified by mutableStateOf(false); private set
    private var generation = 0
    var pending by mutableStateOf(false); private set
    var hasIntent by mutableStateOf(false); private set
    private var intent: InstallationSubmission? = null
    private var job: Job? = null
    init { viewModelScope.launch { session.state.collect { next -> if (next !== owner) {
        job?.cancel(); generation++; owner = next as? SessionState.Active; busy = false; installation = null; sources = emptyList(); catalog = null
        plan = null; recovery = null; dependencies = null; problems = emptyList(); operation = null; operationVerified = false; pending = false; intent = null; hasIntent = false
    } } } }
    fun stop() { job?.cancel(); generation++; busy = false }
    private suspend fun verify(active: SessionState.Active) { currentCoroutineContext().ensureActive(); if (owner !== active || session.state.value !== active) throw CancellationException("Session changed") }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val active = owner ?: return
        if (busy || ServerCapabilities.PROXY_SERVER !in active.capabilities) return
        val current = ++generation
        busy = true; problems = emptyList()
        job = viewModelScope.launch {
            try { verify(active); block(active) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (owner === active && current == generation) problems = listOf("runtime.request_failed") }
            finally { if (owner === active && session.state.value === active && current == generation) busy = false }
        }
    }
    private suspend fun call(active: SessionState.Active, method: String, route: String, body: JsonBody? = null, key: String? = null): String? {
        val result = repository.runtimeCatalog(active, method, route, body, key); verify(active)
        return when (result) {
            is ApiResult.Success -> result.value
            is ApiResult.Problem -> { problems = problems + result.code; null }
            is ApiResult.Transport -> { problems = problems + "runtime.request_failed"; null }
        }
    }
    fun refresh() = work { active -> load(active) }
    private suspend fun load(active: SessionState.Active) {
        installation = null; operationVerified = false; recovery = null; dependencies = null; catalog = null; if (!hasIntent) plan = null
        call(active, "GET", ManagedRuntimeRoutes.installation("sing-box"))?.let {
            require(JSONObject(it).getJSONObject("installation").getString("runtimeId") == "sing-box")
            installation = ManagedRuntimeInstallation(it)
            dependencies = JSONObject(it).let { j -> if (j.isNull("environment")) null else j.getJSONObject("environment").toString() }
        }
        sources = call(active, "GET", ManagedRuntimeRoutes.SOURCES)?.let { ManagedRuntimeWire.sources(it, "sing-box") }.orEmpty()
        recovery = call(active, "GET", ManagedRuntimeRoutes.recovery("sing-box"))
        pending = installations.pending(active).any { it.service == InstallationService.SingBox }
        val result = installations.active(active, InstallationService.SingBox); verify(active)
        if (result is ApiResult.Success) {
            result.value?.let { require(it.service == InstallationService.SingBox); operation = it; operationVerified = true }
            if (result.value == null && operation?.state?.active == true) {
                val receipt = installations.operation(active, requireNotNull(operation).operationId); verify(active)
                if (receipt is ApiResult.Success) { require(receipt.value.service == InstallationService.SingBox); operation = receipt.value; operationVerified = true }
                else { installation = null; problems = problems + "installation.recovery_unknown" }
            } else if (result.value == null) operationVerified = operation != null
        } else { installation = null; problems = problems + "installation.recovery_unknown" }
    }
    fun clearPlan() { if (!busy && !hasIntent) { plan = null; problems = emptyList() } }
    fun catalog(source: String, refresh: Boolean, prerelease: Boolean) = work { active ->
        require(!hasIntent && !pending && sources.count { it.id == source } == 1)
        catalog = null; plan = null
        call(active, if (refresh) "POST" else "GET", ManagedRuntimeRoutes.catalog(source, refresh, prerelease),
            if (refresh) JsonBody() else null, if (refresh) UUID.randomUUID().toString() else null)?.let {
            val value = ManagedRuntimeWire.catalog(it, "sing-box"); require(value.source.id == source)
            catalog = value; sources = sources.map { if (it.id == source) value.source else it }
        }
    }
    fun propose(kind: InstallationKind, release: String?, prerelease: Boolean, keepData: Boolean) = work { active ->
        require(active.privilegedOperations && !pending && !hasIntent && operation?.state?.active != true)
        plan = null; dependencies = null
        val host = requireNotNull(installation)
        val directory = catalog.takeUnless { kind == InstallationKind.Uninstall }
        if (kind != InstallationKind.Uninstall) {
            require(directory?.usable == true && sources.singleOrNull { it.id == directory.source.id } == directory.source)
            require(release != null && host.eligible(kind, release))
            require(directory.releases.any { it.id == release && it.trust == "confirmed" && it.supported && (prerelease || it.channel == "stable") })
        }
        val key = UUID.randomUUID().toString()
        val request = host.request(kind, directory, release, key, prerelease, keepData)
        val body = JsonBody().raw("sourceId", directory?.source?.id?.let(JSONObject::quote) ?: "null").objectField("request", request)
        call(active, "POST", ManagedRuntimeRoutes.plan("sing-box"), body, key)?.let { payload ->
            val json = JSONObject(payload); val list = json.getJSONArray("problems")
            problems = (0 until list.length()).map { list.getString(it) }
            dependencies = if (json.isNull("dependencies")) null else json.getJSONObject("dependencies").toString()
            if (json.getBoolean("allowed") && list.length() == 0 && !json.isNull("plan")) {
                val frozen = ManagedRuntimeInstallationRequest(true, json.getJSONObject("plan").toString())
                require(frozen.service == InstallationService.SingBox && frozen.key == key && frozen.kind == kind)
                val expected = JSONObject(request.toByteArray().decodeToString()); val actual = json.getJSONObject("plan").getJSONObject("request")
                require(expected.keys().asSequence().toSet() == actual.keys().asSequence().toSet())
                require(expected.keys().asSequence().all { expected.get(it).toString() == actual.get(it).toString() })
                plan = frozen
            }
        }
    }
    fun submit(review: ManagedRuntimeInstallationRequest) = work { active ->
        require(active.privilegedOperations && plan === review && operation?.state?.active != true)
        if (intent == null) { require(!pending); intent = installations.prepare(active, review.kind, review); hasIntent = true }
        val result = installations.submit(requireNotNull(intent), answers); verify(active)
        pending = installations.pending(active).any { it.service == InstallationService.SingBox }
        when (result) {
            is ApiResult.Success -> { require(result.value.service == InstallationService.SingBox); operation = result.value; operationVerified = true; intent = null; hasIntent = false; plan = null }
            is ApiResult.Problem -> { problems = listOf(result.code); if (!pending) { intent = null; hasIntent = false } }
            is ApiResult.Transport -> problems = listOf("installation.recovery_unknown")
        }
    }
    fun observe() = work { active ->
        val current = operation ?: return@work
        operationVerified = false
        val result = installations.operation(active, current.operationId); verify(active)
        if (result is ApiResult.Success) {
            require(result.value.service == InstallationService.SingBox); operation = result.value; operationVerified = true
            if (!result.value.state.active) load(active)
        } else problems = listOf("installation.recovery_unknown")
    }
    fun cancel(review: InstallationOperation) = work { active ->
        require(active.privilegedOperations && operationVerified && operation == review && review.state.active && review.cancellable)
        operationVerified = false
        val result = installations.cancel(active, review.operationId); verify(active)
        if (result is ApiResult.Success) { require(result.value.service == InstallationService.SingBox); operation = result.value; operationVerified = true }
        else problems = listOf("installation.recovery_unknown")
    }
    fun acceptFacts() = work { active ->
        val saved = installations.pending(active).firstOrNull { it.service == InstallationService.SingBox } ?: return@work
        val result = installations.acceptCurrentFacts(active, saved); verify(active)
        if (result is ApiResult.Success) { intent = null; hasIntent = false; load(active) } else problems = listOf("installation.recovery_unknown")
    }
    fun recover(action: String, observed: String, confirmed: Boolean) = work { active ->
        require(active.privilegedOperations && recovery == observed)
        val json = JSONObject(observed); require(json.getBoolean("required"))
        if (action == "close") require(confirmed && !json.isNull("recoveryProblemCode") && json.isNull("serviceProblem") && !json.getJSONObject("service").getBoolean("running"))
        val body = JsonBody().string("operationId", InstallationRoutes.canonicalId(json.getString("operationId"))).bool("confirmed", action == "close")
        val result = installations.singBoxRecovery(active, action, body, answers); verify(active)
        if (result !is ApiResult.Success) problems = listOf(if (result is ApiResult.Problem) result.code else "runtime.request_failed")
        installation = null; plan = null; recovery = call(active, "GET", ManagedRuntimeRoutes.recovery("sing-box"))
    }
}
