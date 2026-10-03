package app.relaxkonos.mobile.ui.manage

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.runtime.saveable.rememberSaveable
import android.app.Application
import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
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
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import app.relaxkonos.mobile.core.auth.SessionState

/**
 * Manage navigation and the independently queried process list.
 *
 * The performance page owns its separate foreground read subscription.
 */
class ManageViewModel(application: Application) : AndroidViewModel(application) {
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

/**
 * A manageable domain and the glyph that identifies it.
 *
 * The glyph is a resource id from the desktop icon set (`ui/icons/DesktopIcons.kt`), not a Material
 * `ImageVector`: the phone shows the same marks the desktop shows for the same two domains.
 */
private data class ManageDomain(
    @param:StringRes val titleRes: Int,
    @param:StringRes val subtitleRes: Int,
    @param:DrawableRes val iconRes: Int,
    val open: () -> Unit,
)

/**
 * Manage.
 *
 * Only domains with a working mobile workflow are listed. The design forbids adding an entry just
 * because the desktop has one (`Shell.Design.md` §8). Each entry opens its implemented domain workflow, including Nginx sites and certificate management.
 *
 * The domains sit in one group rather than in one card each: they are alternatives at the same level,
 * and stacking them made a two-item list look like a dashboard.
 */
@Composable
fun ManageScreen(
    onOpenMonitor: () -> Unit,
    onOpenDeployments: () -> Unit,
    onOpenDocker: () -> Unit,
    onOpenGit: () -> Unit,
    onOpenWebsites: () -> Unit,
    onOpenCertificates: () -> Unit,
    onOpenTunnels: () -> Unit,
    onOpenProxy: () -> Unit,
    onOpenSmb: () -> Unit,
    onOpenFirewall: () -> Unit,
    onOpenGuardian: () -> Unit,
    onOpenScripts: () -> Unit,
    onOpenOperations: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = app.relaxkonos.mobile.ui.common.appContainer()

    val domains = buildList {
        if (container.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) ||
            container.capabilities.contains(ServerCapabilities.WEB_SERVER) ||
            container.capabilities.contains(ServerCapabilities.EVENT_ALERTS) ||
            container.capabilities.contains(ServerCapabilities.CERTIFICATES) || container.capabilities.contains(ServerCapabilities.TUNNELS) || container.capabilities.contains(ServerCapabilities.PROXY) || container.capabilities.contains(ServerCapabilities.FIREWALL)) {
            add(ManageDomain(R.string.operations_title, R.string.operations_subtitle,
                DesktopIcons.operations, onOpenOperations))
        }
        if (container.capabilities.contains(ServerCapabilities.DOCKER)) {
            add(ManageDomain(R.string.docker_title, R.string.docker_subtitle, R.drawable.ic_app_docker, onOpenDocker))
        }
        if (container.capabilities.contains(ServerCapabilities.GIT)) {
            add(ManageDomain(R.string.git_title, R.string.git_subtitle, R.drawable.ic_sys_file_git_config, onOpenGit))
        }
        if (container.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) {
            add(ManageDomain(R.string.deployments_title, R.string.deployments_subtitle,
                DesktopIcons.deployments, onOpenDeployments))
        }
        if (container.capabilities.contains(ServerCapabilities.WEB_SERVER)) {
            add(ManageDomain(R.string.websites_title, R.string.websites_subtitle, DesktopIcons.websites, onOpenWebsites))
        }
        if (container.capabilities.contains(ServerCapabilities.CERTIFICATES)) {
            add(ManageDomain(R.string.certificates_title, R.string.certificates_subtitle, DesktopIcons.certificates, onOpenCertificates))
        }
        if (container.capabilities.contains(ServerCapabilities.TUNNELS)) {
            add(ManageDomain(R.string.tunnels_title, R.string.tunnels_subtitle, DesktopIcons.tunnels, onOpenTunnels))
        }
        if (container.capabilities.contains(ServerCapabilities.FILE_SERVICES)) {
            add(ManageDomain(R.string.smb_title, R.string.smb_intro, DesktopIcons.smb, onOpenSmb))
        }
        if (container.capabilities.contains(ServerCapabilities.FIREWALL)) {
            add(ManageDomain(R.string.firewall_title, R.string.firewall_intro, DesktopIcons.firewall, onOpenFirewall))
        }
        if (container.capabilities.contains(ServerCapabilities.PROXY)) {
            add(ManageDomain(R.string.mihomo_title, R.string.mihomo_intro, DesktopIcons.proxy, onOpenProxy))
        }
        if (container.capabilities.contains(ServerCapabilities.GUARDIAN)) {
            add(ManageDomain(R.string.guardian_title, R.string.guardian_subtitle, DesktopIcons.guardian, onOpenGuardian))
            add(ManageDomain(R.string.scripts_title, R.string.scripts_subtitle, DesktopIcons.scripts, onOpenScripts))
        }
        if (container.capabilities.contains(ServerCapabilities.METRICS) || container.capabilities.contains(ServerCapabilities.PROCESSES)) {
            add(ManageDomain(R.string.workspace_taskmanager, R.string.workspace_taskmanager_note, DesktopIcons.monitor, onOpenMonitor))
        }
    }

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.manage_title))

        if (domains.isEmpty()) {
            EmptyState(
                text = stringResource(R.string.error_capability_missing),
                icon = DesktopIcons.notice,
            )
        } else {
            SectionGroup {
                domains.forEach { domain ->
                    ListRow(
                        title = stringResource(domain.titleRes),
                        subtitle = stringResource(domain.subtitleRes),
                        leading = { IconBadge(icon = domain.iconRes) },
                        trailing = { DesktopIcon(icon = DesktopIcons.disclosure, size = 20.dp) },
                        onClick = domain.open,
                    )
                }
            }
        }
    }
}
