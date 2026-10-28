package app.relaxkonos.mobile.ui.manage.components

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentPlanResult
import app.relaxkonos.mobile.core.net.WebServer
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.data.ComponentRepository
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class ComponentChangeReview(
    val componentId: String,
    val change: ComponentChange,
    val keepData: Boolean,
    val plan: ComponentPlanResult,
)

data class ComponentOperationNotice(val operationId: String)

data class NginxChoiceState(val current: String?, val servers: List<WebServer>)

data class ComponentCenterUiState(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val rows: List<ComponentRead> = emptyList(),
    val error: Boolean = false,
    val problemCode: String? = null,
    val busy: Boolean = false,
    val review: ComponentChangeReview? = null,
    val operation: ComponentOperationNotice? = null,
    val actionProblem: String? = null,
    val nginx: NginxChoiceState? = null,
    val nginxLoading: Boolean = false,
) {
    val recoveryVisible: Boolean get() = rows.any { it.view?.recoveryRequired == true }
}

/**
 * Loads component state and runs the three change actions the component center owns on Android:
 * install, update and uninstall. Plans are frozen by the host and applied through the domain
 * installation ledger; this class never invents a package, version or elevated command.
 */
class ComponentCenterViewModel(
    private val session: AuthSession,
    private val repository: ComponentRepository,
) : ViewModel() {
    private val mutable = MutableStateFlow(ComponentCenterUiState())
    val state = mutable.asStateFlow()

    private var owner: SessionState.Active? = null
    private var work: Job? = null
    private var action: Job? = null

    init {
        viewModelScope.launch {
            session.state.collect { value ->
                val active = value as? SessionState.Active
                if (owner !== active) {
                    work?.cancel()
                    action?.cancel()
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
                is ApiResult.Success -> mutable.value.copy(rows = result.value, loading = false, loaded = true)
                is ApiResult.Problem -> ComponentCenterUiState(error = true, loaded = true, problemCode = result.code)
                is ApiResult.Transport -> ComponentCenterUiState(error = true, loaded = true)
            }
        }
    }

    fun refresh(active: SessionState.Active) {
        if (mutable.value.loading || session.state.value !== active) return
        load(active)
    }

    /** Freezes the change and opens the confirmation; a refused plan becomes a visible problem. */
    fun requestChange(active: SessionState.Active, componentId: String, change: ComponentChange,
        keepData: Boolean = true) {
        if (mutable.value.busy || session.state.value !== active) return
        val component = mutable.value.rows.firstOrNull { it.componentId == componentId }?.view?.state ?: return
        // The host only grants changes to an absent (install) or managed (update/uninstall) component.
        if (change == ComponentChange.Install) { if (component.ownership != "absent") return }
        else if (component.ownership != "managed") return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try {
                repository.plan(active, componentId, change, component, keepData)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> if (result.value.allowed) {
                    mutable.value.copy(busy = false, review = ComponentChangeReview(componentId, change, keepData, result.value))
                } else {
                    mutable.value.copy(busy = false,
                        actionProblem = result.value.problems.firstOrNull() ?: "component.plan_refused")
                }
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "component.plan_refused")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "component.unreachable")
            }
        }
    }

    /** Applies the reviewed plan through the installation ledger, including the elevation prompt. */
    fun confirmChange(active: SessionState.Active, provider: ElevationAnswerProvider) {
        val review = mutable.value.review ?: return
        if (mutable.value.busy || session.state.value !== active || review.componentId.isEmpty()) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try {
                repository.apply(active, review.plan, provider)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(busy = false, review = null,
                    operation = ComponentOperationNotice(result.value.operationId))
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "installation.failed")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "installation.unreachable")
            }
            if (result is ApiResult.Success) load(active)
        }
    }

    fun dismissReview() { mutable.value = mutable.value.copy(review = null) }

    fun dismissOperation() { mutable.value = mutable.value.copy(operation = null) }

    fun dismissActionProblem() { mutable.value = mutable.value.copy(actionProblem = null) }

    /** Loads the host-global Nginx selection together with the managed/integrated instances. */
    fun openNginxSelection(active: SessionState.Active) {
        if (session.state.value !== active) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(nginxLoading = true)
            val current = try { repository.currentNginx(active) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            if (current !is ApiResult.Success) {
                mutable.value = mutable.value.copy(nginxLoading = false, actionProblem = (current as? ApiResult.Problem)?.code ?: "component.nginx_unavailable")
                return@launch
            }
            val servers = try { repository.nginxServers(active) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            mutable.value = when (servers) {
                is ApiResult.Success -> mutable.value.copy(nginxLoading = false,
                    nginx = NginxChoiceState(current.value, servers.value.filter { it.providerId == "nginx" }))
                is ApiResult.Problem -> mutable.value.copy(nginxLoading = false, actionProblem = servers.code ?: "component.nginx_unavailable")
                is ApiResult.Transport -> mutable.value.copy(nginxLoading = false, actionProblem = "component.nginx_unavailable")
            }
        }
    }

    fun selectNginx(active: SessionState.Active, serverId: String) {
        if (session.state.value !== active || mutable.value.nginx == null) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try { repository.selectCurrentNginx(active, serverId) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(busy = false,
                    nginx = mutable.value.nginx?.copy(current = result.value))
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "component.nginx_unavailable")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "component.nginx_unavailable")
            }
        }
    }

    fun closeNginxSelection() { mutable.value = mutable.value.copy(nginx = null) }
}
