package app.relaxkonos.mobile.ui.manage

import android.app.Application
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RemoteProcess
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import kotlinx.coroutines.launch

/**
 * Manage destination state: host metrics and the process list.
 *
 * Both live in one holder because the destination is one capability domain on the phone and two panes
 * on a tablet; splitting them would make the tablet layout fetch the same data twice.
 */
class ManageViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    var snapshot by mutableStateOf<PerformanceSnapshot?>(null)
        private set

    var monitorLoading by mutableStateOf(false)
        private set

    var monitorMessage by mutableStateOf<UiMessage?>(null)
        private set

    var processItems by mutableStateOf<List<RemoteProcess>>(emptyList())
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

    fun openPane(route: String) {
        expandedPane = route
    }

    fun loadMonitor() {
        if (!metricsAvailable || monitorLoading) {
            return
        }
        monitorLoading = true
        monitorMessage = null
        viewModelScope.launch {
            when (val result = container.system.performance()) {
                is ApiResult.Success -> snapshot = result.value
                else -> monitorMessage = result.failureMessage()
            }
            monitorLoading = false
        }
    }

    fun dismissMonitorMessage() {
        monitorMessage = null
    }

    fun dismissProcessMessage() {
        processMessage = null
    }

    /** Not named `setProcessFilter`: the `processFilter` property already emits that JVM setter. */
    fun updateProcessFilter(value: String) {
        processFilter = value
    }

    fun loadProcesses(page: Int = processPage) {
        if (!processesAvailable || processesLoading) {
            return
        }
        processPage = page.coerceAtLeast(1)
        processesLoading = true
        processMessage = null
        viewModelScope.launch {
            when (val result = container.system.processes(processPage, PAGE_SIZE, processFilter.takeIf { it.isNotBlank() })) {
                is ApiResult.Success -> {
                    processItems = result.value.items
                    processTotalCount = result.value.totalCount
                }

                else -> processMessage = result.failureMessage()
            }
            processesLoading = false
        }
    }

    val hasNextPage: Boolean get() = processPage * PAGE_SIZE < processTotalCount

    fun requestKill(process: RemoteProcess) {
        killTarget = process
    }

    fun cancelKill() {
        killTarget = null
    }

    /**
     * Ends the process.
     *
     * `force` decides between a graceful and an immediate termination, and the server treats both as an
     * elevation-required operation, so the elevation dialog appears before anything is sent.
     */
    fun confirmKill(force: Boolean) {
        val target = killTarget ?: return
        killTarget = null
        viewModelScope.launch {
            processesLoading = true
            when (val result = container.system.killProcess(target.pid, force, container.elevations, container.elevationAnswers)) {
                is ApiResult.Success -> loadProcesses()
                else -> processMessage = result.failureMessage()
            }
            processesLoading = false
        }
    }

    private companion object {
        const val PAGE_SIZE = 50
    }
}

/**
 * Manage.
 *
 * Only domains with a working mobile workflow are listed. The design forbids adding an entry just
 * because the desktop has one (`RelaxKonOS.Mobile.V1.Design.md` §8), so Docker, Guardian, deployments
 * and web servers simply do not appear until V1-E implements them.
 */
@Composable
fun ManageScreen(
    onOpenMonitor: () -> Unit,
    onOpenProcesses: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = app.relaxkonos.mobile.ui.common.appContainer()

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(stringResource(R.string.manage_title), style = MaterialTheme.typography.headlineSmall)

        val domains = buildList {
            if (container.capabilities.contains(ServerCapabilities.METRICS)) {
                add(Triple(stringResource(R.string.manage_monitor_title), stringResource(R.string.manage_monitor_subtitle), onOpenMonitor))
            }
            if (container.capabilities.contains(ServerCapabilities.PROCESSES)) {
                add(Triple(stringResource(R.string.manage_processes_title), stringResource(R.string.manage_processes_subtitle), onOpenProcesses))
            }
        }

        if (domains.isEmpty()) {
            EmptyHint(stringResource(R.string.error_capability_missing))
        } else {
            domains.forEach { (title, subtitle, open) ->
                Card(Modifier.fillMaxWidth().clickable { open() }) {
                    Row(
                        modifier = Modifier.padding(16.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(title, style = MaterialTheme.typography.titleMedium)
                            Text(
                                subtitle,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null)
                    }
                }
            }
        }
    }
}
