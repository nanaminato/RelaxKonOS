package app.relaxkonos.mobile.ui.home

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import android.app.Application
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import kotlinx.coroutines.launch
/**
 * Home destination state.
 *
 * The snapshot is fetched through `SystemRepository`, which already owns the single-refresh retry, so
 * this holder only tracks what the screen must render. It keeps no host data beyond the last answer: a
 * stale snapshot rendered as fresh is worse than no snapshot.
 */
class HomeViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    var snapshot by mutableStateOf<PerformanceSnapshot?>(null)
        private set

    var message by mutableStateOf<UiMessage?>(null)
        private set

    var loading by mutableStateOf(false)
        private set

    val metricsAvailable: Boolean get() = container.capabilities.contains(ServerCapabilities.METRICS)

    val recentOperations get() = container.recentOperations.entries

    fun connectionDescription(serviceId: String): String? {
        container.loginTunnels.all().firstOrNull { it.serviceId == serviceId }?.let {
            return "${it.host}:${it.port} → ${it.remoteUrl}"
        }
        container.managedLogins.hostFor(serviceId)?.let { return it.displayName }
        return serviceId.takeIf { it.startsWith("http://") || it.startsWith("https://") }
    }

    fun refresh() {
        if (!metricsAvailable || loading) {
            return
        }
        loading = true
        message = null
        viewModelScope.launch {
            when (val result = container.system.performance()) {
                is ApiResult.Success -> snapshot = result.value
                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    fun dismissMessage() {
        message = null
    }
}
