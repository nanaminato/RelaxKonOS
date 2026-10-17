package app.relaxkonos.mobile.ui.manage.guardian

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.GuardianRepository
import kotlinx.coroutines.CancellationException
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.GuardianApproval
import app.relaxkonos.mobile.core.net.GuardianDefinition
import app.relaxkonos.mobile.core.net.GuardianLog
import app.relaxkonos.mobile.core.net.GuardianStatus
import app.relaxkonos.mobile.core.net.GuardianWorkload
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
data class GuardianUiState(
    val loading: Boolean = false,
    val status: GuardianStatus? = null,
    val workloads: List<GuardianWorkload> = emptyList(),
    val selectedId: String? = null,
    val definition: GuardianDefinition? = null,
    val logs: List<GuardianLog> = emptyList(),
    val error: Boolean = false,
    val problemCode: String? = null,
    val unknown: Boolean = false,
    val stale: Boolean = true,
)

internal class GuardianActionReview(val owner: SessionState.Active, val target: GuardianWorkload, val action: String)

class GuardianViewModel(private val session: AuthSession, private val repository: GuardianRepository) : ViewModel() {
    internal var actionReview by mutableStateOf<GuardianActionReview?>(null)
        private set
    internal fun dismissAction() { actionReview = null }
    internal fun requestAction(active: SessionState.Active, target: GuardianWorkload, action: String) {
        val state = mutable.value
        if (current(active) && !state.loading && !state.stale && !state.unknown && editing == null &&
            state.selectedId == target.id && target in state.workloads && action in setOf("start", "stop", "restart", "delete"))
            actionReview = GuardianActionReview(active, target, action)
    }
    internal fun confirmAction(review: GuardianActionReview) {
        val state = mutable.value
        if (actionReview !== review) return
        actionReview = null
        if (!current(review.owner) || state.loading || state.stale || state.unknown || editing != null ||
            state.selectedId != review.target.id || review.target !in state.workloads) return
        if (review.action == "delete") delete(review.target.id) else act(review.target.id, review.action)
    }
    private val mutable = MutableStateFlow(GuardianUiState())
    val state = mutable.asStateFlow()
    var editing by mutableStateOf<GuardianDraft?>(null)
        private set
    private var owner: SessionState.Active? = null
    private var work: Job? = null
    private var reading: Job? = null
    private var readingGeneration = 0
    private var selection: Job? = null
    private var selectionGeneration = 0
    private var observation: Job? = null
    internal val logObserver = GuardianLogObserver(session, viewModelScope)
    init { viewModelScope.launch { session.state.collect { value ->
        val active = value as? SessionState.Active
        if (owner !== active) { dismissAction(); work?.cancel(); reading?.cancel(); selection?.cancel(); observation?.cancel(); logObserver.stop(); owner = active; editing = null; mutable.value = GuardianUiState() }
    } } }
    private fun current(active: SessionState.Active) = owner === active && session.state.value === active
    fun observe(active: SessionState.Active, visible: Boolean) {
        if (visible && session.state.value === active && owner !== active) load(active)
        observation?.cancel(); observation = null
        if (!visible || editing != null || !current(active)) { stopObserving(); return }
        if (mutable.value.status == null && !mutable.value.error && !mutable.value.unknown) load(active)
        val id = mutable.value.selectedId
        if (id != null && mutable.value.definition == null && selection?.isActive != true) select(id)
        logObserver.observe(active, id)
        observation = viewModelScope.launch {
            while (current(active)) {
                load(active, reconcile = false)
                delay(3_000)
            }
        }
    }
    fun stopObserving() {
        dismissAction()
        observation?.cancel(); observation = null
        val wasReading = reading?.isActive == true
        readingGeneration++
        selectionGeneration++
        reading?.cancel(); reading = null
        selection?.cancel(); selection = null
        logObserver.stop()
        if (wasReading) mutable.update {
            it.copy(loading = false, status = null, definition = null, logs = emptyList(), stale = true)
        }
    }
    fun load(active: SessionState.Active, reconcile: Boolean = true) {
        if (session.state.value !== active) return
        if (reconcile) dismissAction() else if (actionReview != null) return
        if (owner !== active) { work?.cancel(); reading?.cancel(); selection?.cancel(); observation?.cancel(); logObserver.stop(); owner = active; editing = null; mutable.value = GuardianUiState() }
        if (mutable.value.loading) return
        val request = ++readingGeneration
        mutable.update { it.copy(loading = true, error = !reconcile && it.error, problemCode = if (reconcile) null else it.problemCode) }
        reading = viewModelScope.launch {
            try {
                val status = repository.status(active)
                if (!current(active) || request != readingGeneration) return@launch
                val workloads = repository.workloads(active)
                if (!current(active) || request != readingGeneration) return@launch
                mutable.update { old -> old.copy(status = (status as? ApiResult.Success)?.value,
                    workloads = (workloads as? ApiResult.Success)?.value ?: old.workloads,
                    unknown = if (reconcile && status is ApiResult.Success && workloads is ApiResult.Success) false else old.unknown,
                    stale = (!reconcile && old.stale) || status !is ApiResult.Success || workloads !is ApiResult.Success || status.value.running != true,
                    error = (!reconcile && old.error) || status !is ApiResult.Success || workloads !is ApiResult.Success,
                    problemCode = old.problemCode.takeIf { !reconcile && old.error }
                        ?: (workloads as? ApiResult.Problem)?.code ?: (status as? ApiResult.Problem)?.code
                        ?: (status as? ApiResult.Success)?.value?.problemCode?.takeIf(String::isNotBlank)) }
                if (workloads is ApiResult.Success && mutable.value.selectedId != null && workloads.value.none { it.id == mutable.value.selectedId }) select(null)
            } catch (cancelled: CancellationException) {
                if (request == readingGeneration) readFailed(active)
                throw cancelled
            } finally { if (current(active) && request == readingGeneration) mutable.update { it.copy(loading = false) } }
        }
    }
    fun select(id: String?) {
        dismissAction()
        val request = ++selectionGeneration
        selection?.cancel()
        mutable.update { it.copy(selectedId = id, definition = null, logs = emptyList(), error = id == null && it.error) }
        val active = owner ?: return
        if (id == null || !current(active)) return
        selection = viewModelScope.launch {
            try {
                val definition = repository.definition(active, id)
                val logs = repository.logs(active, id)
                if (!current(active) || request != selectionGeneration || mutable.value.selectedId != id) return@launch
                mutable.update { it.copy(definition = (definition as? ApiResult.Success)?.value, logs = (logs as? ApiResult.Success)?.value.orEmpty(),
                    stale = it.stale || definition !is ApiResult.Success || logs !is ApiResult.Success,
                    error = definition !is ApiResult.Success || logs !is ApiResult.Success,
                    problemCode = (definition as? ApiResult.Problem)?.code ?: (logs as? ApiResult.Problem)?.code) }
            } catch (cancelled: CancellationException) {
                if (current(active) && request == selectionGeneration && mutable.value.selectedId == id) readFailed(active)
                throw cancelled
            }
        }
    }
    fun beginEdit(value: GuardianDefinition, creating: Boolean) { if (!mutable.value.loading && !mutable.value.unknown && !mutable.value.stale) editing = GuardianDraft(value, creating) }
    fun cancelEdit() { if (!mutable.value.loading) editing = null }
    fun readEditor() {
        val draft = editing ?: return; val active = owner ?: return
        if (!current(active) || mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false) }
        work = viewModelScope.launch {
            try {
                val result = repository.definition(active, draft.initial.id)
                if (!current(active) || editing !== draft) return@launch
                if (result is ApiResult.Success) { editing = GuardianDraft(result.value, false); mutable.update { it.copy(unknown = false, stale = false, error = false, definition = result.value) } }
                else if (result is ApiResult.Problem && result.status == 404 && draft.creating) mutable.update { it.copy(unknown = false, stale = false, error = false) }
                else {
                    readFailed(active)
                    mutable.update { it.copy(problemCode = (result as? ApiResult.Problem)?.code) }
                }
            } catch (cancelled: CancellationException) {
                readFailed(active)
                throw cancelled
            } finally { if (current(active)) mutable.update { it.copy(loading = false) } }
        }
    }
    fun save(draft: GuardianDraft, definition: GuardianDefinition, approval: GuardianApproval?) {
        val active = owner
        if (active == null || !current(active) || mutable.value.loading || mutable.value.unknown || mutable.value.stale || editing !== draft) { approval?.password?.fill('\u0000'); return }
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        work = viewModelScope.launch {
            try {
                val result = repository.save(active, draft.initial.takeUnless { draft.creating }, definition, approval)
                if (!current(active)) return@launch
                if (result is ApiResult.Success) {
                    editing = null; mutable.update { it.copy(definition = result.value, selectedId = result.value.id) }
                } else failure(result)
            } catch (cancelled: CancellationException) {
                if (current(active)) failure(ApiResult.Transport(null))
                throw cancelled
            } finally {
                approval?.password?.fill('\u0000')
                if (current(active)) { mutable.update { it.copy(loading = false) }; if (editing == null) load(active) }
            }
        }.also { job -> job.invokeOnCompletion { approval?.password?.fill('\u0000') } }
    }
    fun act(id: String, action: String) = mutate { active -> repository.action(active, id, action) }
    fun delete(id: String) = mutate { active -> repository.delete(active, id) }
    private fun mutate(call: suspend (SessionState.Active) -> ApiResult<app.relaxkonos.mobile.core.net.GuardianOperation>) {
        val active = owner ?: return
        if (!current(active) || mutable.value.loading || mutable.value.unknown || mutable.value.stale) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        work = viewModelScope.launch {
            try { val result = call(active); if (current(active) && result !is ApiResult.Success) failure(result) }
            catch (cancelled: CancellationException) { if (current(active)) failure(ApiResult.Transport(null)); throw cancelled }
            finally { if (current(active)) { mutable.update { it.copy(loading = false) }; if (!mutable.value.error) load(active) } }
        }
    }
    private fun failure(result: ApiResult<*>) { mutable.update { it.copy(error = true, unknown = result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500,
        problemCode = (result as? ApiResult.Problem)?.code) } }
    private fun readFailed(active: SessionState.Active) {
        if (current(active)) mutable.update { it.copy(status = null, definition = null, logs = emptyList(), stale = true, error = true, problemCode = null) }
    }
}
