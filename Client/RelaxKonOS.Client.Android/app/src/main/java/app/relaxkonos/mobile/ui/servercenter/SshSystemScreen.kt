package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshSystemProbe
import app.relaxkonos.mobile.servercenter.SshSystemSnapshot
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.MetricTile
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusChip
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatUptime
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Fixed, read-only Windows and Linux host inspection.  The UI never accepts a command string from the user. */
class SshSystemViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    private val mutableState = MutableStateFlow(SshSystemUiState())
    val state = mutableState.asStateFlow()

    fun refresh(hostId: String) {
        if (mutableState.value.loading) return
        val secret = container.serverCenter.workspacePasswordCopy()
        if (secret == null) {
            mutableState.update { it.copy(problem = true, loading = false) }
            return
        }
        mutableState.update { it.copy(loading = true, problem = false) }
        viewModelScope.launch {
            try {
                val snapshot = container.serverCenterConnections.connect(
                    hostId,
                    SshCredential(SshCredentialKind.Password, secret, null),
                    System.currentTimeMillis(),
                ).use { session ->
                    SshSystemProbe.read(session.sshTransport)
                }
                mutableState.update { it.copy(snapshot = snapshot ?: it.snapshot, loading = false, problem = snapshot == null) }
            } catch (_: Exception) {
                mutableState.update { it.copy(loading = false, problem = true) }
            } finally {
                secret.fill('\u0000')
            }
        }
    }

}

data class SshSystemUiState(
    val loading: Boolean = false,
    val snapshot: SshSystemSnapshot? = null,
    val problem: Boolean = false,
)

@Composable
fun SshSystemScreen(hostId: String, onExit: () -> Unit, modifier: Modifier = Modifier) {
    val model: SshSystemViewModel = viewModel(key = "ssh-system-$hostId")
    val state by model.state.collectAsState()
    val host = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    // 工作区里唯一一处主机切换入口。放在系统页：文件、终端与部署页各自在讲自己的事，
    // 「我在哪台主机上、换一台」是对这台主机的总览，与这里的资源状态同源。
    var switcherOpen by remember(hostId) { mutableStateOf(false) }
    LaunchedEffect(hostId) { model.refresh(hostId) }
    val snapshot = state.snapshot
    Column(
        // 多了当前主机卡片之后这一页不再保证一屏放得下，因此改成可滚动：小屏上至少能滚到退出按钮。
        modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        SectionCard(
            title = host?.displayName ?: hostId,
            subtitle = host?.let { "${it.sshUserName}@${it.sshHost}:${it.sshPort}" },
            leading = DesktopIcons.host,
            trailing = {
                TextButton(onClick = { switcherOpen = true }) {
                    Text(stringResource(R.string.server_center_switch_host))
                }
            },
        ) {
            StatusChip(
                text = stringResource(R.string.server_center_current_host),
                tone = StatusTone.Primary,
            )
        }
        SectionCard(
            title = stringResource(R.string.ssh_workspace_system),
            subtitle = stringResource(R.string.ssh_workspace_system_subtitle, host?.displayName ?: hostId),
            leading = DesktopIcons.system,
            trailing = {
                IconButton(onClick = { model.refresh(hostId) }, enabled = !state.loading) {
                    DesktopIcon(DesktopIcons.refresh, contentDescription = stringResource(R.string.common_refresh))
                }
            },
        ) {
            when {
                state.loading && snapshot == null -> CircularProgressIndicator()
                snapshot != null -> {
                    SystemMetrics(snapshot)
                    if (state.problem) Text(stringResource(R.string.ssh_workspace_system_unavailable), color = MaterialTheme.colorScheme.error)
                }
                state.problem -> Text(stringResource(R.string.ssh_workspace_system_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        OutlinedButton(onClick = onExit, modifier = Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.ssh_workspace_exit))
        }
    }
    if (switcherOpen) {
        SshHostSwitcherDialog(currentHostId = hostId, onDismiss = { switcherOpen = false })
    }
}

@Composable
private fun SystemMetrics(snapshot: SshSystemSnapshot) {
    val memoryFraction = snapshot.memoryUsedBytes.toDouble().div(snapshot.memoryTotalBytes).toFloat().coerceIn(0f, 1f)
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
        MetricTile(
            label = stringResource(R.string.home_label_cpu),
            value = stringResource(R.string.home_value_percent, snapshot.cpuPercent),
            progress = (snapshot.cpuPercent / 100.0).toFloat(),
            tone = StatusTone.Primary,
            modifier = Modifier.weight(1f),
        )
        MetricTile(
            label = stringResource(R.string.home_label_memory),
            value = stringResource(R.string.home_value_percent, memoryFraction * 100),
            supporting = stringResource(
                R.string.home_value_used_of_total,
                formatSize(snapshot.memoryUsedBytes).orEmpty(),
                formatSize(snapshot.memoryTotalBytes).orEmpty(),
            ),
            progress = memoryFraction,
            tone = StatusTone.Primary,
            modifier = Modifier.weight(1f),
        )
    }
    snapshot.disks.forEach { disk ->
        MetricTile(
            label = stringResource(R.string.ssh_workspace_system_disk, disk.name),
            value = stringResource(R.string.home_value_percent, disk.usedBytes.toDouble() / disk.totalBytes * 100),
            supporting = stringResource(R.string.home_value_used_of_total, formatSize(disk.usedBytes).orEmpty(), formatSize(disk.totalBytes).orEmpty()),
            progress = (disk.usedBytes.toDouble() / disk.totalBytes).toFloat(),
            tone = StatusTone.Primary,
            modifier = Modifier.fillMaxWidth(),
        )
    }
    KeyValueRow(stringResource(R.string.home_label_uptime), formatUptime(snapshot.uptimeSeconds))
    if (snapshot.system.isNotBlank()) KeyValueRow(stringResource(R.string.ssh_workspace_system_platform), snapshot.system)
}
