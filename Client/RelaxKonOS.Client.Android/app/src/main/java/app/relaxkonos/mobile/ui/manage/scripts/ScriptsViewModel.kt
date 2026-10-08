package app.relaxkonos.mobile.ui.manage.scripts

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.ScriptTaskRepository
import app.relaxkonos.mobile.data.ScriptRequestJournal
import app.relaxkonos.mobile.data.PendingScriptRequest
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ScriptRequest
import app.relaxkonos.mobile.core.net.ScriptTask
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
data class ScriptsUiState(val loading: Boolean = false, val tasks: List<ScriptTask> = emptyList(),
    val selected: ScriptTask? = null, val error: Boolean = false, val problemCode: String? = null,
    val draft: ScriptDraft? = null, val pending: PendingScriptRequest? = null, val journalAvailable: Boolean = true)

class ScriptsViewModel(private val session: AuthSession, private val repository: ScriptTaskRepository,
    private val journal: ScriptRequestJournal) : ViewModel() {
    suspend fun observeSelected() {
        val active = owner ?: return
        val id = state.value.selected?.id ?: return
        while (owner === active) {
            kotlinx.coroutines.delay(2000)
            val selected = state.value.selected ?: return
            if (selected.id != id || selected.state !in setOf("queued", "running", "cancelling") || state.value.error || state.value.draft != null) return
            if (!state.value.loading) readSelected(active, id, selectionVersion)
        }
    }

    private val mutable = MutableStateFlow(ScriptsUiState())
    val state = mutable.asStateFlow()
    private var owner: SessionState.Active? = null
    private var selectionVersion = 0L
    private var selectedId: String? = null
    private var selectionJob: Job? = null
    private var loadJob: Job? = null
    private var writeJob: Job? = null

    init {
        viewModelScope.launch {
            session.state.collect { current ->
                if (owner != null && current !== owner) reset(null)
            }
        }
    }

    private fun reset(active: SessionState.Active?) {
        selectionVersion++
        selectionJob?.cancel(); loadJob?.cancel(); writeJob?.cancel()
        selectedId = null
        owner = active
        mutable.value = ScriptsUiState()
    }

    private fun owns(active: SessionState.Active) = owner === active && session.state.value === active

    fun openEditor() {
        val active = owner ?: return
        if (!owns(active) || !state.value.journalAvailable || state.value.loading || state.value.draft != null ||
            state.value.pending != null) return
        selectionJob?.cancel(); selectionVersion++
        mutable.update { it.copy(draft = ScriptDraft(active.userName), error = false, problemCode = null) }
    }

    fun updateDraft(draft: ScriptDraft) {
        val active = owner ?: return
        if (!owns(active) || !state.value.journalAvailable || state.value.loading || state.value.draft == null ||
            state.value.pending != null) return
        mutable.update { it.copy(draft = draft) }
    }

    fun closeEditor() {
        if (!state.value.loading) mutable.update { it.copy(draft = null) }
    }

    fun load(active: SessionState.Active) {
        if (session.state.value !== active) return
        if (owner !== active) reset(active)
        if (mutable.value.loading || mutable.value.draft != null) return
        if (!restorePending(active)) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        loadJob = viewModelScope.launch {
            val result = repository.tasks(active)
            if (!owns(active)) return@launch
            mutable.update { old -> when (result) {
                is ApiResult.Success -> old.copy(loading = false, tasks = result.value.tasks,
                    error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank))
                is ApiResult.Problem -> old.copy(loading = false, error = true, problemCode = result.code)
                is ApiResult.Transport -> old.copy(loading = false, error = true, problemCode = null)
            } }
            if (state.value.pending != null) {
                mutable.update { it.copy(loading = true, error = true, problemCode = "scripts.write_unknown") }
                verifyPending(active)
                if (owns(active)) mutable.update { it.copy(loading = false) }
            }
        }
    }

    fun select(id: String) {
        val active = owner ?: return
        if (!owns(active) || !state.value.journalAvailable || writeJob?.isActive == true || state.value.draft != null || state.value.pending != null) return
        selectionJob?.cancel()
        val version = ++selectionVersion
        selectedId = id
        mutable.update { it.copy(selected = it.selected.takeIf { task -> task?.id == id }) }
        selectionJob = viewModelScope.launch { readSelected(active, id, version) }
    }

    private suspend fun readSelected(active: SessionState.Active, id: String, version: Long) {
        val result = repository.task(active, id)
        if (!owns(active) || selectionVersion != version || selectedId != id) return
        when (result) {
            is ApiResult.Success -> {
                val valid = result.value.success && result.value.task?.id == id
                mutable.update { it.copy(selected = if (valid) result.value.task else it.selected,
                    error = !valid, problemCode = result.value.problemCode.takeIf(String::isNotBlank)) }
            }
            else -> mutable.update { it.copy(error = true, problemCode = (result as? ApiResult.Problem)?.code) }
        }
    }

    fun submit(request: ScriptRequest) {
        val active = owner ?: run { request.approval?.password?.fill('\u0000'); return }
        if (!owns(active) || !state.value.journalAvailable || state.value.draft == null || state.value.loading || writeJob?.isActive == true ||
            state.value.pending != null) { request.approval?.password?.fill('\u0000'); return }
        selectionJob?.cancel(); selectionVersion++
        val key = UUID.randomUUID().toString()
        val pending = PendingScriptRequest(key, false)
        if (!persistPending(active, pending)) { request.approval?.password?.fill('\u0000'); return }
        mutable.update { it.copy(loading = true, error = false, problemCode = null,
            pending = pending) }
        writeJob = viewModelScope.launch {
            if (!markAttempted(active)) return@launch
            val result = try { repository.submit(active, request, key) }
                finally { request.approval?.password?.fill('\u0000') }
            if (!owns(active)) return@launch
            when (result) {
                is ApiResult.Success -> {
                    val accepted = result.value.success && result.value.task?.id == key
                    val unknown = !accepted && (result.value.success || uncertainAgentResult(result.value.problemCode))
                    if (!unknown && !completePending(active)) return@launch
                    selectedId = result.value.task?.id.takeIf { accepted }
                    mutable.update { it.copy(loading = false, selected = if (accepted) result.value.task else it.selected,
                        draft = if (accepted) null else it.draft,
                        pending = it.pending.takeIf { unknown },
                        error = !accepted, problemCode = if (unknown) "scripts.write_unknown"
                            else result.value.problemCode.takeIf(String::isNotBlank)) }
                    if (accepted) load(active)
                }
                else -> {
                    val rejected = result is ApiResult.Problem && result.status in setOf(400, 401, 403, 404, 409, 422)
                    if (rejected && !completePending(active)) return@launch
                    mutable.update { it.copy(loading = false, error = true, pending = it.pending.takeUnless { rejected },
                        problemCode = if (rejected) (result as ApiResult.Problem).code else "scripts.write_unknown") }
                }
            }
        }.also { job -> job.invokeOnCompletion { request.approval?.password?.fill('\u0000') } }
    }

    fun cancel(id: String) {
        val active = owner ?: return
        if (!owns(active) || !state.value.journalAvailable || state.value.loading || writeJob?.isActive == true || state.value.pending != null || state.value.draft != null) return
        val pending = PendingScriptRequest(id, true)
        if (!persistPending(active, pending)) return
        selectionJob?.cancel(); selectionVersion++
        mutable.update { it.copy(loading = true, error = false, problemCode = null,
            pending = pending) }
        writeJob = viewModelScope.launch {
            if (!markAttempted(active)) return@launch
            val result = repository.cancel(active, id)
            if (!owns(active)) return@launch
            if (result is ApiResult.Success && result.value.success && result.value.task?.id == id) {
                if (!completePending(active)) return@launch
                mutable.update { it.copy(pending = null) }
                selectedId = id
                readSelected(active, id, selectionVersion)
                mutable.update { it.copy(loading = false) }
            }
            else {
                val unknown = result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500 ||
                    result is ApiResult.Success && (result.value.success || uncertainAgentResult(result.value.problemCode))
                if (!unknown && !completePending(active)) return@launch
                mutable.update { it.copy(loading = false, error = true, pending = it.pending.takeIf { unknown },
                    problemCode = if (unknown) "scripts.write_unknown" else when (result) {
                        is ApiResult.Problem -> result.code
                        is ApiResult.Success -> result.value.problemCode.takeIf(String::isNotBlank)
                        is ApiResult.Transport -> "scripts.write_unknown"
                    }) }
            }
        }
    }

    private fun uncertainAgentResult(code: String) = code in setOf("guardian.agent_timeout", "guardian.agent_unavailable")

    fun verifyRequest() {
        val active = owner ?: return
        if (!owns(active) || state.value.loading) return
        if (!restorePending(active) || state.value.pending == null) return
        mutable.update { it.copy(loading = true) }
        loadJob = viewModelScope.launch {
            verifyPending(active)
            if (owns(active)) mutable.update { it.copy(loading = false) }
        }
    }

    private suspend fun verifyPending(active: SessionState.Active) {
        val pending = state.value.pending ?: return
        val result = repository.task(active, pending.taskId)
        if (!owns(active) || state.value.pending != pending) return
        val task = (result as? ApiResult.Success)?.value?.takeIf { it.success }?.task?.takeIf { it.id == pending.taskId }
        val resolved = task != null && (!pending.cancellation || task.state in setOf("cancelling", "cancelled", "succeeded", "failed", "timedOut", "interrupted"))
        if (resolved) {
            if (!completePending(active)) return
            selectedId = pending.taskId
            selectionVersion++
            mutable.update { it.copy(selected = task, pending = null, error = false, problemCode = null,
                draft = if (pending.cancellation) it.draft else null) }
        } else mutable.update { it.copy(selected = task ?: it.selected, error = true, problemCode = "scripts.write_unknown") }
    }

    private fun storageFailure() {
        mutable.update { it.copy(loading = false, journalAvailable = false, error = true,
            problemCode = "scripts.storage_failed") }
    }

    private fun restorePending(active: SessionState.Active): Boolean = try {
        val stored = journal.pending(active)
        // This marker is persisted before entering the repository: false proves no write was started.
        val pending = if (stored != null && !stored.attempted) {
            journal.complete(active, stored); null
        } else stored
        mutable.update { it.copy(pending = pending, journalAvailable = true,
            error = pending != null, problemCode = "scripts.write_unknown".takeIf { pending != null }) }
        true
    } catch (_: Exception) { storageFailure(); false }

    private fun persistPending(active: SessionState.Active, pending: PendingScriptRequest): Boolean = try {
        journal.begin(active, pending); true
    } catch (_: Exception) { storageFailure(); false }

    private fun markAttempted(active: SessionState.Active): Boolean {
        if (!owns(active)) return false
        val pending = state.value.pending ?: return false
        return try {
            val attempted = journal.attempted(active, pending)
            mutable.update { it.copy(pending = attempted) }
            true
        } catch (_: Exception) { storageFailure(); false }
    }

    private fun completePending(active: SessionState.Active): Boolean = try {
        state.value.pending?.let { journal.complete(active, it) }; true
    } catch (_: Exception) { storageFailure(); false }
}
