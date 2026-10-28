package app.relaxkonos.mobile.ui.manage.components

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.data.ComponentRepository
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class ComponentCenterUiState(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val rows: List<ComponentRead> = emptyList(),
    val error: Boolean = false,
    val problemCode: String? = null,
) {
    val recoveryVisible: Boolean get() = rows.any { it.view?.recoveryRequired == true }
}

/**
 * Loads one state per component. Every result is tagged with the session that asked for it, so a
 * response from an earlier account or server can never populate the current screen.
 */
class ComponentCenterViewModel(
    private val session: AuthSession,
    private val repository: ComponentRepository,
) : ViewModel() {
    private val mutable = MutableStateFlow(ComponentCenterUiState())
    val state = mutable.asStateFlow()

    private var owner: SessionState.Active? = null
    private var work: Job? = null

    init {
        viewModelScope.launch {
            session.state.collect { value ->
                val active = value as? SessionState.Active
                if (owner !== active) {
                    work?.cancel()
                    owner = active
                    mutable.value = ComponentCenterUiState()
                }
            }
        }
    }

    fun load(active: SessionState.Active) {
        owner = active
        if (mutable.value.loading) return
        work?.cancel()
        work = viewModelScope.launch {
            mutable.value = mutable.value.copy(loading = true, error = false, problemCode = null)
            val result = try {
                repository.read(active, ComponentCatalog.ids)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> ComponentCenterUiState(rows = result.value, loaded = true)
                is ApiResult.Problem -> ComponentCenterUiState(error = true, loaded = true, problemCode = result.code)
                is ApiResult.Transport -> ComponentCenterUiState(error = true, loaded = true)
            }
        }
    }

    fun refresh(active: SessionState.Active) {
        if (mutable.value.loading || session.state.value !== active) return
        load(active)
    }
}
