package app.relaxkonos.mobile.ui.manage.monitor

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ServerCapabilities
import kotlinx.coroutines.launch

internal class MonitorViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private val observer = PerformanceObserver(container.session, container.system, viewModelScope)
    val state = observer.state
    private var owner = container.session.state.value as? SessionState.Active
    private var visible = false
    var selected by mutableStateOf<PerformanceResource?>(null)
        private set
    val available get() = container.capabilities.contains(ServerCapabilities.METRICS)
    init {
        viewModelScope.launch { container.session.state.collect { value ->
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
