package app.relaxkonos.mobile.ui.manage.monitor

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ServerCapabilities
import kotlinx.coroutines.launch

internal class MonitorViewModel(
    private val session: AuthSession,
    system: SystemRepository,
    factory: PerformanceConnectionFactory = PerformanceConnectionFactory { url, token, snapshot, closed ->
        app.relaxkonos.mobile.core.net.ServerPerformanceConnection(url, token, snapshot, closed)
    },
) : ViewModel() {
    private val observer = PerformanceObserver(session, system, viewModelScope, factory)
    val state = observer.state
    private var owner = session.state.value as? SessionState.Active
    private var visible = false
    var selected by mutableStateOf<PerformanceResource?>(null)
        private set
    val available get() = (session.state.value as? SessionState.Active)?.capabilities?.contains(ServerCapabilities.METRICS) == true
    init {
        viewModelScope.launch { session.state.collect { value ->
            val active = value as? SessionState.Active
            if (owner !== active) { observer.stop(); owner = active; selected = null; if (visible && active != null && available) observer.observe(active) }
        } }
    }
    fun observe() { visible = true; owner?.takeIf { available }?.let(observer::observe) }
    fun stopObserving() { visible = false; observer.stop() }
    fun retry() { if (visible) owner?.let(observer::retry) }
    fun dismissProblem() = observer.dismissProblem()
    fun select(resource: PerformanceResource?) { selected = resource }
}
