package app.relaxkonos.mobile.ui.manage.squid

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class SquidDraft(val address: String = "127.0.0.1", val port: String = "3128", val remote: Boolean = false,
    val networks: String = "127.0.0.1/32\n::1/128", val allowed: String = "", val denied: String = "",
    val parent: String = "", val parentPort: String = "7890", val adopt: Boolean = false, val accessMode: SquidAccessMode = SquidAccessMode.IpWhitelist) {
    fun configuration(): SquidConfiguration {
        fun lines(value: String) = value.split('\n', '\r', ',').map(String::trim).filter(String::isNotEmpty)
        val p = port.toIntOrNull(); require(p != null && p in 1024..65535) { "squid.invalid_port" }
        val upstream = if (parent.isBlank()) null else {
            val upstreamPort = parentPort.toIntOrNull(); require(upstreamPort != null && upstreamPort in 1..65535) { "squid.invalid_port" }
            SquidUpstream(parent.trim(), upstreamPort)
        }
        return SquidConfiguration(address.trim(), p, remote, lines(networks), lines(allowed), lines(denied), upstream, accessMode)
    }
    companion object {
        fun from(c: SquidConfiguration) = SquidDraft(c.bindAddress, c.port.toString(), c.allowRemoteClients,
            c.allowedNetworks.joinToString("\n"), c.allowedDomains.joinToString("\n"), c.deniedDomains.joinToString("\n"),
            c.upstream?.host ?: "", (c.upstream?.port ?: 7890).toString(), accessMode = c.accessMode)
    }
}
internal data class SquidState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val snapshot: SquidSnapshot? = null, val draft: SquidDraft = SquidDraft(), val initial: SquidDraft = SquidDraft(),
    val initialized: Boolean = false, val preview: String = "", val validation: SquidValidation? = null,
    val pending: PendingSquidRequest? = null, val operation: ProxyOperation? = null, val problem: String? = null)
internal class SquidViewModel(private val session: AuthSession, private val repository: SquidRepository) : ViewModel() {
    var state by mutableStateOf(SquidState()); private set
    private var job: Job? = null
    private var generation = 0
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; state = SquidState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false) }
    fun edit(draft: SquidDraft) { if (!state.busy && state.pending == null) state = state.copy(draft = draft, preview = "", validation = null) }
    fun refresh() = work { owner -> load(owner) }
    fun loadSaved() { if (!state.busy && state.pending == null) {
        val draft = SquidDraft.from(state.snapshot?.configuration ?: SquidConfiguration())
        state = state.copy(draft = draft, initial = draft, preview = "", validation = null)
    } }
    fun preview() = work { owner ->
        val result = repository.preview(owner, state.draft.configuration()); verify(owner)
        state = when (result) {
            is ApiResult.Success -> state.copy(preview = result.value.configuration ?: result.value.errors.joinToString("\n"), problem = if (result.value.valid) null else "squid.invalid")
            else -> state.copy(problem = problem(result))
        }
    }
    fun validate() = work { owner ->
        val result = repository.validate(owner, state.draft.configuration()); verify(owner)
        state = if (result is ApiResult.Success) state.copy(validation = result.value) else state.copy(problem = problem(result))
    }
    fun apply() = work { owner ->
        require(owner.controller && owner.privilegedOperations && state.pending == null)
        require(state.snapshot?.status?.let { it.supported && it.installed && !it.recoveryRequired } == true)
        val request = SquidRequest(state.draft.configuration(), state.draft.adopt)
        val preview = repository.preview(owner, requireNotNull(request.configuration)); verify(owner)
        if (preview !is ApiResult.Success) { state = state.copy(problem = problem(preview)); return@work }
        state = state.copy(preview = preview.value.configuration ?: preview.value.errors.joinToString("\n"))
        if (!preview.value.valid) { state = state.copy(problem = "squid.invalid"); return@work }
        watch(owner, repository.begin(owner, request))
    }
    fun lifecycle(action: SquidAction) = work { owner ->
        require(owner.controller && owner.privilegedOperations && state.pending == null)
        val status = requireNotNull(state.snapshot?.status)
        require(status.supported && status.installed)
        require(action in setOf(SquidAction.Stop, SquidAction.Disable) || (!status.recoveryRequired && status.managed))
        watch(owner, repository.begin(owner, SquidRequest(action = action)))
    }
    fun resume() = work { owner -> watch(owner, repository.resume(owner)) }
    private suspend fun watch(owner: SessionState.Active, result: ApiResult<String>) {
        verify(owner)
        if (result !is ApiResult.Success) { state = state.copy(problem = problem(result)); return }
        repeat(90) {
            val operation = repository.operation(owner, result.value); verify(owner)
            if (operation !is ApiResult.Success) { state = state.copy(problem = problem(operation)); return }
            state = state.copy(operation = operation.value)
            if (!operation.value.state.active) { load(owner); return }
            delay(1000)
        }
        state = state.copy(problem = "squid.pending")
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = repository.snapshot(owner); verify(owner)
        if (result is ApiResult.Success) {
            val draft = if (state.initialized) state.draft else SquidDraft.from(result.value.configuration ?: SquidConfiguration())
            state = state.copy(snapshot = result.value, draft = draft, initial = if (state.initialized) state.initial else draft, initialized = true)
        } else state = state.copy(snapshot = null, problem = problem(result))
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.GATEWAY_PROXY !in owner.capabilities) return
        val current = generation
        state = state.copy(busy = true, problem = null)
        job = viewModelScope.launch {
            try { verify(owner); state = state.copy(pending = repository.pending(owner)); block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (exception: Exception) { if (isCurrent(owner, current)) state = state.copy(problem = if (exception.message == "squid.invalid_port") exception.message else "squid.unavailable") }
            finally { if (isCurrent(owner, current)) {
                val pending = try { repository.pending(owner) } catch (_: Exception) { state.pending }
                if (isCurrent(owner, current)) state = state.copy(busy = false, pending = pending)
            } }
        }
    }
    private fun isCurrent(owner: SessionState.Active, current: Int) = state.owner === owner && session.state.value === owner && generation == current
    private suspend fun verify(owner: SessionState.Active) { currentCoroutineContext().ensureActive(); if (state.owner !== owner || session.state.value !== owner) throw CancellationException("Squid session changed") }
    private fun problem(result: ApiResult<*>): String = when (result) { is ApiResult.Problem -> result.code; is ApiResult.Transport -> "squid.unavailable"; is ApiResult.Success -> error("Expected failure") }
}
