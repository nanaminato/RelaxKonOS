package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*
import java.time.Instant

internal data class TailscaleLoginUiState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val records: List<TailscaleReference> = emptyList(), val selected: String? = null,
    val operation: TailscaleLoginOperation? = null, val recovery: TailscaleLoginRecovery? = null,
    val problem: String? = null, val storageFailed: Boolean = false, val browserOpened: Boolean = false)
internal enum class TailscaleLoginAvailability { Unknown, NeedLogin, DeviceApproval, AlreadyConnected, AlreadyStopped, Starting, Unavailable }
/** The first-login page must refuse to start on a host that is already signed in or cannot run login. */
internal fun tailscaleLoginAvailability(status: TailscaleStatus?): TailscaleLoginAvailability {
    if (status == null) return TailscaleLoginAvailability.Unknown
    if (status.state != "observed") return TailscaleLoginAvailability.Unavailable
    return when (status.backendState) {
        "NeedsLogin" -> TailscaleLoginAvailability.NeedLogin
        "NeedsMachineAuth" -> TailscaleLoginAvailability.DeviceApproval
        "Running" -> TailscaleLoginAvailability.AlreadyConnected
        "Stopped" -> TailscaleLoginAvailability.AlreadyStopped
        "Starting" -> TailscaleLoginAvailability.Starting
        else -> TailscaleLoginAvailability.Unknown
    }
}
internal val TailscaleLoginAvailability.allowsStart get() = this == TailscaleLoginAvailability.NeedLogin
internal class TailscaleLoginReview(val owner: SessionState.Active, val plan: TailscaleLoginOperation?, val recovery: TailscaleLoginRecovery?)
internal class TailscaleLoginViewModel(private val session: AuthSession, private val repository: TailscaleLoginRepository,
    private val answers: ElevationAnswerProvider) : ViewModel() {
    var state by mutableStateOf(TailscaleLoginUiState()); private set
    var review by mutableStateOf<TailscaleLoginReview?>(null); private set
    private var reviewActive by mutableStateOf(false)
    private var generation = 0
    private var foregroundEpoch = 0L
    private var job: Job? = null
    val canStart get() = reviewActive && !state.busy && !state.storageFailed && state.owner?.privilegedOperations == true
        && state.records.none { it.phase != TailscaleReferencePhase.Known }
    val canOpen get() = reviewActive && !state.busy && state.operation?.let { operation ->
        operation.operationId == state.selected && operation.state == TailscaleLoginState.Issued && operation.closure == null && operation.expiresAt > Instant.now()
            && state.records.any { it.operationId == operation.operationId && it.phase != TailscaleReferencePhase.Closing }
    } == true
    val canInspect get() = reviewActive && !state.busy && state.operation?.let {
        it.operationId == state.selected && it.closure == null && it.state in TailscaleLoginRepository.recoverable
    } == true
    init { viewModelScope.launch { session.state.collect { next ->
        if (state.owner !== next) { job?.cancel(); generation++; review = null; state = TailscaleLoginUiState(owner = next as? SessionState.Active) }
    } } }
    fun setActive(active: Boolean) { reviewActive = active; if (!active) { foregroundEpoch++; review = null } }
    fun stop() { setActive(false); job?.cancel(); generation++; review = null; state = state.copy(busy = false,operation = null,recovery = null,browserOpened = false) }
    fun dismiss() { if (!state.busy) review = null }
    fun loadRecords() { work { owner -> loadReferences(owner) } }
    fun select(id: String) {
        if (state.busy || state.records.none { it.operationId == id }) return
        review = null; state = state.copy(selected = id,operation = null,recovery = null,browserOpened = false)
    }
    fun prepare() {
        if (!canStart) return
        val gesture = foregroundEpoch
        review = null
        work { owner ->
            if (!reviewActive || foregroundEpoch != gesture) return@work
            val result = repository.prepare(owner,answers); verify(owner); loadReferences(owner)
            state = state.copy(operation = (result as? ApiResult.Success)?.value,recovery = null,browserOpened = false,problem = problem(result))
            if (result is ApiResult.Success) {
                state = state.copy(selected = result.value.operationId)
                if (reviewActive && foregroundEpoch == gesture) review = TailscaleLoginReview(owner,result.value,null)
            }
        }
    }
    fun submit(expected: TailscaleLoginReview, hostMayHaveChanged: () -> Unit) {
        if (!reviewActive || review !== expected || state.busy || state.owner !== expected.owner || session.state.value !== expected.owner) return
        review = null
        val expiresAt = expected.plan?.expiresAt ?: expected.recovery?.expiresAt
        if (expiresAt == null || expiresAt <= Instant.now()) { state = state.copy(problem = "tailscale.plan_expired"); return }
        work { owner ->
            hostMayHaveChanged()
            state = state.copy(recovery = null,browserOpened = false)
            val result = if (expected.plan != null) repository.execute(owner,expected.plan,true,answers)
                else repository.close(owner,requireNotNull(expected.recovery),true,answers)
            verify(owner)
            state = state.copy(operation = (result as? ApiResult.Success)?.value,problem = problem(result))
            loadReferences(owner)
        }
    }
    fun read() {
        val id = state.selected ?: return
        work { owner ->
            state = state.copy(operation = null,recovery = null,browserOpened = false)
            val result = repository.read(owner,id); verify(owner)
            state = state.copy(operation = (result as? ApiResult.Success)?.value,problem = problem(result)); loadReferences(owner)
        }
    }
    fun open(browser: (String) -> Boolean) {
        if (!canOpen) return
        val operation = state.operation ?: return
        val request = generation
        val gesture = foregroundEpoch
        work { owner ->
            if (!reviewActive || foregroundEpoch != gesture) return@work
            state = state.copy(browserOpened = false)
            val result = repository.handoff(owner,operation,answers) { url ->
                if (current(owner,request) && reviewActive && foregroundEpoch == gesture && state.selected == operation.operationId) browser(url) else false
            }
            verify(owner)
            if (foregroundEpoch != gesture) return@work
            state = state.copy(browserOpened = result is ApiResult.Success && result.value,
                problem = if (result is ApiResult.Problem && result.code == "tailscale.operation.not_found") "tailscale.login.challenge_unavailable"
                    else if (result is ApiResult.Success && !result.value && reviewActive) "tailscale.login.browser_failed" else problem(result))
        }
    }
    fun inspect() {
        if (!canInspect) return
        val operation = state.operation ?: return
        work { owner ->
            state = state.copy(recovery = null)
            val result = repository.recovery(owner,operation,answers); verify(owner)
            state = state.copy(recovery = (result as? ApiResult.Success)?.value,problem = problem(result))
        }
    }
    fun requestClose() {
        val owner = state.owner ?: return
        val preview = state.recovery ?: return
        if (!canInspect || !owner.privilegedOperations || !preview.canClose || preview.operationId != state.selected
            || preview.expiresAt == null || preview.expiresAt <= Instant.now()) return
        review = TailscaleLoginReview(owner,null,preview)
    }
    private fun loadReferences(owner: SessionState.Active) {
        try {
            val records = repository.records(owner)
            state = state.copy(records = records,selected = state.selected?.takeIf { id -> records.any { it.operationId == id } } ?: records.firstOrNull()?.operationId,storageFailed = false)
        } catch (_: Exception) { state = state.copy(records = emptyList(),storageFailed = true,operation = null,problem = "tailscale.storage_failed") }
    }
    private fun problem(result: ApiResult<*>) = when(result) {
        is ApiResult.Success -> null; is ApiResult.Problem -> result.code; is ApiResult.Transport -> "tailscale.unverified"
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.TAILSCALE !in owner.capabilities) return
        val request = generation; state = state.copy(busy = true,problem = null)
        job = viewModelScope.launch {
            try { verify(owner); block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner,request)) state = state.copy(problem = "tailscale.unverified") }
            finally { if (current(owner,request)) { loadReferences(owner); state = state.copy(busy = false) } }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (session.state.value !== owner || state.owner !== owner) throw CancellationException("Tailscale login session changed")
    }
}
