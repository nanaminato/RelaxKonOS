package app.relaxkonos.mobile.ui.manage

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import android.app.Application
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProcessSort
import app.relaxkonos.mobile.core.net.RemoteProcess
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.manage.processes.refreshedProcessSelection
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import app.relaxkonos.mobile.core.auth.SessionState
/**
 * Manage navigation and the independently queried process list.
 *
 * The performance page owns its separate foreground read subscription.
 */
class ManageViewModel(application: Application) : AndroidViewModel(application) {
    suspend fun observeProcesses() {
        try {
            startProcessObserving()
            while (true) {
                kotlinx.coroutines.delay(6000)
                loadProcesses()
            }
        } finally { stopProcessObserving() }
    }

    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container
    private var owner = container.session.state.value as? SessionState.Active
    private var processJob: Job? = null
    private var killJob: Job? = null
    private var processGeneration = 0
    private var processObserving = false
    private var appliedFilter: String? = null
    private var killOwner: SessionState.Active? = null
    var killMessage by mutableStateOf<UiMessage?>(null)
        private set
    private fun current(active: SessionState.Active) = owner === active && container.session.state.value === active

    var processSelected by mutableStateOf<RemoteProcess?>(null)
        private set

    var processItems by mutableStateOf<List<RemoteProcess>>(emptyList())
        private set

    var processSampledAt by mutableStateOf<String?>(null)
        private set
    var processSort by mutableStateOf(ProcessSort.Cpu)
        private set
    var processDescending by mutableStateOf(true)
        private set

    var processTotalCount by mutableStateOf(0)
        private set

    var processPage by mutableStateOf(1)
        private set

    var processFilter by mutableStateOf("")
        private set

    var processesLoading by mutableStateOf(false)
        private set

    var processMessage by mutableStateOf<UiMessage?>(null)
        private set

    var killTarget by mutableStateOf<RemoteProcess?>(null)
        private set

    val metricsAvailable: Boolean get() = container.capabilities.contains(ServerCapabilities.METRICS)

    val processesAvailable: Boolean get() = container.capabilities.contains(ServerCapabilities.PROCESSES)

    /**
     * Which content pane the Expanded layout is showing.
     *
     * The Expanded layout renders a domain beside the list instead of pushing a page, so the selection
     * is held here rather than on the navigation stack — that is what keeps the system back key exiting
     * the destination instead of unwinding a list the user can still see (`…V1.Design.md` §4.1).
     */
    var expandedPane by mutableStateOf<String?>(null)
        private set

    init { viewModelScope.launch { container.session.state.collect { value ->
        val active = value as? SessionState.Active
        if (owner !== active) {
            stopProcessObserving(); killJob?.cancel(); owner = active
            processSelected = null; appliedFilter = null; processSampledAt = null; processSort = ProcessSort.Cpu; processDescending = true; processItems = emptyList(); processTotalCount = 0; processPage = 1; processFilter = ""
            processesLoading = false; processMessage = null
            killTarget = null; killOwner = null; killMessage = null
        }
    } } }

    fun openPane(route: String) {
        expandedPane = route
    }

    fun dismissProcessMessage() {
        processMessage = null
    }

    /** Not named `setProcessFilter`: the `processFilter` property already emits that JVM setter. */
    fun updateProcessFilter(value: String) {
        processFilter = value.take(512)
    }

    fun searchProcesses() {
        if (processesLoading) return
        appliedFilter = processFilter.trim().takeIf { it.isNotEmpty() }; clearProcessQuery()
        loadProcesses(1)
    }
    fun changeProcessSort(sort: ProcessSort) {
        if (processesLoading) return
        processSort = sort; clearProcessQuery(); loadProcesses(1)
    }
    fun toggleProcessDirection() {
        if (processesLoading) return
        processDescending = !processDescending; clearProcessQuery(); loadProcesses(1)
    }
    private fun clearProcessQuery() {
        processSelected = null; processItems = emptyList(); processTotalCount = 0; processSampledAt = null; processPage = 1; cancelKill()
    }
    fun startProcessObserving() { processObserving = true; loadProcesses() }
    fun stopProcessObserving() {
        processObserving = false
        processGeneration++; processJob?.cancel(); processJob = null
        if (killJob?.isActive != true) processesLoading = false
        cancelKill()
    }

    fun loadProcesses(page: Int = processPage) {
        val active = owner ?: return
        if (!current(active) || !processesAvailable || !processObserving || processesLoading) return
        val requestedPage = page.coerceAtLeast(1); val filter = appliedFilter
        val sort = processSort; val descending = processDescending; val generation = ++processGeneration
        processesLoading = true; processMessage = null
        processJob = viewModelScope.launch {
            var reload = false
            try {
                val result = container.system.processes(active, requestedPage, PAGE_SIZE, filter, sort, descending)
                if (!current(active) || generation != processGeneration) return@launch
                when (result) { is ApiResult.Success -> { processPage = minOf(requestedPage, (result.value.totalCount - 1).coerceAtLeast(0) / PAGE_SIZE + 1); reload = processPage != requestedPage; processSampledAt = if (reload) null else result.value.sampledAt; processItems = result.value.items; processSelected = refreshedProcessSelection(processSelected, processItems); processTotalCount = result.value.totalCount }
                    else -> processMessage = result.failureMessage() }
            } finally { if (current(active) && generation == processGeneration) { processesLoading = false; if (reload) loadProcesses() } }
        }
    }

    val hasNextPage: Boolean get() = processPage * PAGE_SIZE < processTotalCount

    fun selectProcess(process: RemoteProcess?) {
        processSelected = process?.let { target -> processItems.firstOrNull { it.pid == target.pid && it.startTime == target.startTime } }
    }

    fun requestKill(process: RemoteProcess) {
        val active = owner ?: return
        if (current(active) && !processesLoading && process.startTime != null && processItems.any { it.pid == process.pid && it.startTime == process.startTime }) {
            killTarget = process; killOwner = active
        }
    }
    fun cancelKill() { killTarget = null; killOwner = null }
    fun dismissKillMessage() { killMessage = null }
    /** The Server terminates only this PID/start-time instance with its actual OS permissions. */
    fun confirmKill() {
        val target = killTarget ?: return; val active = killOwner ?: return; val startTime = target.startTime ?: return
        if (!current(active) || processesLoading) { cancelKill(); return }
        cancelKill(); processesLoading = true; killMessage = null
        killJob = viewModelScope.launch {
            var refresh = false
            try {
                val result = container.system.killProcess(active, target.pid, startTime)
                if (!current(active)) return@launch
                when (result) {
                    is ApiResult.Success -> {
                        val receipt = result.value
                        if (receipt.success) container.recentOperations.record(RecentOperationKind.EndProcess, target.name)
                        else killMessage = UiMessage(when {
                            receipt.requiresElevation -> R.string.manage_processes_host_permission
                            receipt.problemCode == "process.instance_changed" || receipt.problemCode == "process.not_found" -> R.string.manage_processes_instance_changed
                            receipt.problemCode == "process.termination_unverified" -> R.string.manage_processes_kill_unknown
                            else -> R.string.manage_processes_kill_failed
                        }, debugDetail = receipt.problemCode)
                        refresh = true
                    }
                    is ApiResult.Transport -> killMessage = UiMessage(R.string.manage_processes_kill_unknown)
                    is ApiResult.Problem -> killMessage = result.failureMessage()
                }
            } finally { if (current(active)) { processesLoading = false; if (refresh && processObserving) loadProcesses() } }
        }
    }

    private companion object {
        const val PAGE_SIZE = 50
    }
}
