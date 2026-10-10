package app.relaxkonos.mobile.ui.home

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.data.RecentOperationJournal
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.CancellationException
/**
 * Home destination state.
 *
 * The snapshot is fetched through `SystemRepository`, which already owns the single-refresh retry, so
 * this holder only tracks what the screen must render. It keeps no host data beyond the last answer: a
 * stale snapshot rendered as fresh is worse than no snapshot.
 */
class HomeViewModel(
    private val session: AuthSession,
    private val system: SystemRepository,
    private val recent: RecentOperationJournal,
    val connectionDescription: (String) -> String?,
) : ViewModel() {
    private var owner = session.state.value as? SessionState.Active
    private var generation = 0
    private var readJob: Job? = null

    private fun resetOwner(active: SessionState.Active?) {
        generation++
        readJob?.cancel(); readJob = null
        owner = active
        snapshot = null; message = null; loading = false
    }

    var snapshot by mutableStateOf<PerformanceSnapshot?>(null)
        private set

    var message by mutableStateOf<UiMessage?>(null)
        private set

    var loading by mutableStateOf(false)
        private set

    val metricsAvailable: Boolean get() = (session.state.value as? SessionState.Active)?.capabilities?.contains(ServerCapabilities.METRICS) == true

    val recentOperations get() = recent.entries

    init {
        viewModelScope.launch {
            session.state.collect { value ->
                val active = value as? SessionState.Active
                if (owner !== active) resetOwner(active)
            }
        }
    }

    fun refresh() {
        val active = session.state.value as? SessionState.Active ?: return
        if (owner !== active) resetOwner(active)
        if (!metricsAvailable || loading) {
            return
        }
        loading = true
        message = null
        val version = ++generation
        fun current() = generation == version && session.state.value === active
        readJob = viewModelScope.launch {
            try {
                val result = system.performance(active)
                if (!current()) return@launch
                when (result) {
                    is ApiResult.Success -> snapshot = result.value
                    else -> { snapshot = null; message = result.failureMessage() }
                }
            } catch (cancelled: CancellationException) {
                if (current()) snapshot = null
                throw cancelled
            } finally {
                if (current()) loading = false
            }
        }
    }

    fun dismissMessage() {
        message = null
    }
}
