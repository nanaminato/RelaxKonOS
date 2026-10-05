package app.relaxkonos.mobile.ui.manage.scripts

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ScriptRequest
import app.relaxkonos.mobile.core.net.ScriptTask
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
data class ScriptsUiState(val loading: Boolean = false, val tasks: List<ScriptTask> = emptyList(),
    val selected: ScriptTask? = null, val error: Boolean = false, val problemCode: String? = null)

class ScriptsViewModel(application: Application) : AndroidViewModel(application) {
    suspend fun observeSelected() {
        val active = owner ?: return
        val id = state.value.selected?.id ?: return
        while (owner === active) {
            kotlinx.coroutines.delay(2000)
            val selected = state.value.selected ?: return
            if (selected.id != id || selected.state !in setOf("queued", "running", "cancelling")) return
            select(id)
        }
    }

    private val container = getApplication<RelaxKonApplication>().container
    private val mutable = MutableStateFlow(ScriptsUiState())
    val state = mutable.asStateFlow()
    private var owner: SessionState.Active? = null

    fun load(active: SessionState.Active) {
        if (owner !== active) { owner = active; mutable.value = ScriptsUiState() }
        if (mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        viewModelScope.launch {
            val result = container.scriptTasks.tasks(active)
            if (owner !== active) return@launch
            mutable.update { old -> when (result) {
                is ApiResult.Success -> old.copy(loading = false, tasks = result.value.tasks,
                    error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank))
                is ApiResult.Problem -> old.copy(loading = false, error = true, problemCode = result.code)
                is ApiResult.Transport -> old.copy(loading = false, error = true, problemCode = null)
            } }
        }
    }

    fun select(id: String) {
        val active = owner ?: return
        viewModelScope.launch {
            val result = container.scriptTasks.task(active, id)
            if (owner !== active) return@launch
            when (result) {
                is ApiResult.Success -> mutable.update { it.copy(selected = result.value.task,
                    error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank)) }
                else -> mutable.update { it.copy(error = true) }
            }
        }
    }

    fun submit(request: ScriptRequest) {
        val active = owner ?: run { request.approval?.password?.fill('\u0000'); return }
        mutable.update { it.copy(loading = true, error = false) }
        val key = UUID.randomUUID().toString()
        viewModelScope.launch {
            val result = try { container.scriptTasks.submit(active, request, key) }
                finally { request.approval?.password?.fill('\u0000') }
            if (owner !== active) return@launch
            when (result) {
                is ApiResult.Success -> {
                    mutable.update { it.copy(loading = false, selected = result.value.task,
                        error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank)) }
                    load(active)
                }
                else -> mutable.update { it.copy(loading = false, error = true) }
            }
        }
    }

    fun cancel(id: String) {
        val active = owner ?: return
        viewModelScope.launch {
            val result = container.scriptTasks.cancel(active, id)
            if (owner !== active) return@launch
            if (result is ApiResult.Success && result.value.success) select(id)
            else mutable.update { it.copy(error = true) }
        }
    }
}
