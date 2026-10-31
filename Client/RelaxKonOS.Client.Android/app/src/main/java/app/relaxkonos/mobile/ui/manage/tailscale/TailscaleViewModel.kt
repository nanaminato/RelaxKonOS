package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*
import java.time.Instant

internal data class TailscaleState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val control: TailscaleControl? = null, val records: List<TailscaleReference> = emptyList(),
    val selected: String? = null, val operation: TailscaleOperation? = null, val recovery: TailscaleRecovery? = null,
    val problem: String? = null, val storageFailed: Boolean = false)
internal class TailscaleReview(val owner: SessionState.Active, val baseline: TailscaleControl?,
    val plan: TailscaleOperation?, val recovery: TailscaleRecovery?)
internal class TailscaleViewModel(private val session: AuthSession, private val repository: TailscaleControlRepository,
    private val answers: ElevationAnswerProvider) : ViewModel() {
    var state by mutableStateOf(TailscaleState()); private set
    var review by mutableStateOf<TailscaleReview?>(null); private set
    var viewEpoch by mutableStateOf(0L); private set
    private var job: Job? = null
    private var generation = 0
    private var foregroundEpoch = 0L
    private var reviewActive by mutableStateOf(false)
    val editable get() = reviewActive && !state.busy && !state.storageFailed && state.owner?.privilegedOperations == true && state.control?.canControl == true
        && state.records.none { it.phase != TailscaleReferencePhase.Known }
    init { viewModelScope.launch { session.state.collect { owner ->
        if (state.owner !== owner) {
            job?.cancel(); generation++; review = null; state = TailscaleState(owner = owner as? SessionState.Active)
        }
    } } }
    fun setActive(value: Boolean) { reviewActive = value; if (!value) { foregroundEpoch++; review = null } }
    fun stop() { setActive(false); job?.cancel(); generation++; state = state.copy(busy = false,control = null,recovery = null); review = null }
    fun dismiss() { if (!state.busy) review = null }
    fun invalidateControl() { review = null; state = state.copy(control = null,recovery = null) }
    fun loadRecords() { work { owner -> loadReferences(owner) } }
    fun loadControl() { review = null; work { owner ->
        state = state.copy(control = null,recovery = null)
        val result = repository.control(owner,answers); verify(owner)
        state = state.copy(control = (result as? ApiResult.Success)?.value,problem = problem(result))
        loadReferences(owner)
    } }
    fun select(id: String) {
        if (state.busy || state.records.none { it.operationId == id }) return
        review = null; state = state.copy(selected = id,operation = null,recovery = null)
    }
    fun authorizeView() {
        work { owner ->
            when (val result = repository.authorizeView(owner,answers)) {
                is ApiResult.Success -> viewEpoch++
                is ApiResult.Problem -> state = state.copy(problem = result.code)
                is ApiResult.Transport -> state = state.copy(problem = "tailscale.unverified")
            }
        }
    }
    fun prepare(action: TailscaleAction, configuration: TailscaleConfiguration? = null,
        preferences: TailscalePreferences? = null) {
        if (!editable) return
        val baseline = state.control ?: return
        val gesture = foregroundEpoch
        review = null
        work { owner ->
            if (!reviewActive || foregroundEpoch != gesture) return@work
            val result = repository.prepare(owner,baseline,action,configuration,answers,preferences); verify(owner)
            loadReferences(owner)
            if (result is ApiResult.Success) {
                state = state.copy(selected = result.value.operationId,operation = result.value,recovery = null)
                if (reviewActive && foregroundEpoch == gesture) review = TailscaleReview(owner,baseline,result.value,null)
            } else state = state.copy(problem = problem(result))
        }
    }
    fun submit(expected: TailscaleReview) {
        if (!reviewActive || review !== expected || state.busy || state.owner !== expected.owner || session.state.value !== expected.owner) return
        review = null
        val expires = expected.plan?.expiresAt ?: expected.recovery?.expiresAt
        if (expires == null || expires <= Instant.now()) { state = state.copy(problem = "tailscale.plan_expired"); return }
        work { owner ->
            state = state.copy(control = null,recovery = null)
            val result = if (expected.plan != null) repository.execute(owner,expected.plan,true,answers)
                else repository.close(owner,requireNotNull(expected.recovery),true,answers)
            verify(owner)
            state = state.copy(operation = (result as? ApiResult.Success)?.value,problem = problem(result))
            if (result is ApiResult.Success) viewEpoch++
            loadReferences(owner)
        }
    }
    fun read() {
        val id = state.selected ?: return
        work { owner ->
            state = state.copy(operation = null,recovery = null,control = null)
            val result = repository.read(owner,id); verify(owner)
            state = state.copy(operation = (result as? ApiResult.Success)?.value,problem = problem(result))
            loadReferences(owner)
        }
    }
    fun inspect() {
        val id = state.selected ?: return
        if (state.operation?.state != TailscaleOperationState.Unknown || state.operation?.closure != null) return
        work { owner ->
            state = state.copy(recovery = null)
            val result = repository.recovery(owner,id,answers); verify(owner)
            state = state.copy(recovery = (result as? ApiResult.Success)?.value,problem = problem(result))
        }
    }
    fun requestClose() {
        val owner = state.owner ?: return
        val preview = state.recovery ?: return
        if (!reviewActive || state.busy || !owner.privilegedOperations || !preview.canClose || preview.operationId != state.selected
            || preview.expiresAt == null || preview.expiresAt <= Instant.now()) return
        review = TailscaleReview(owner,null,null,preview)
    }
    private fun loadReferences(owner: SessionState.Active) {
        try {
            val records = repository.records(owner)
            state = state.copy(records = records,selected = state.selected?.takeIf { id -> records.any { it.operationId == id } } ?: records.firstOrNull()?.operationId,storageFailed = false)
        } catch (_: Exception) { state = state.copy(records = emptyList(),storageFailed = true,control = null,problem = "tailscale.storage_failed") }
    }
    private fun problem(result: ApiResult<*>) = when (result) {
        is ApiResult.Problem -> result.code
        is ApiResult.Transport -> "tailscale.unverified"
        is ApiResult.Success -> null
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.TAILSCALE !in owner.capabilities) return
        val request = generation; state = state.copy(busy = true,problem = null)
        job = viewModelScope.launch {
            try { verify(owner); block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner,request)) state = state.copy(control = null,problem = "tailscale.unverified") }
            finally { if (current(owner,request)) { loadReferences(owner); state = state.copy(busy = false) } }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && request == generation
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (session.state.value !== owner || state.owner !== owner) throw CancellationException("Tailscale session changed")
    }
}
