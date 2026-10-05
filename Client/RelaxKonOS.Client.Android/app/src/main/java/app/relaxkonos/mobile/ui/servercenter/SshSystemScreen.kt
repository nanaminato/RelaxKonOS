package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshSystemSnapshot
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

@Composable
fun SshSystemScreen(hostId: String, onExit: () -> Unit, modifier: Modifier = Modifier) {
    val model: SshSystemViewModel = viewModel(key = "ssh-system-$hostId")
    val state by model.state.collectAsStateWithLifecycle()
    val host = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    // 工作区里唯一一处主机切换入口。放在系统页：文件、终端与部署页各自在讲自己的事，
    // 「我在哪台主机上、换一台」是对这台主机的总览，与这里的资源状态同源。
    var switcherOpen by remember(hostId) { mutableStateOf(false) }
    LaunchedEffect(hostId) { model.refresh(hostId) }
    val snapshot = state.snapshot
    Column(modifier.fillMaxSize()) {
    Column(
        Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(Spacing.lg),
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
    }
        OutlinedButton(onClick = onExit, modifier = Modifier.fillMaxWidth().padding(horizontal = Spacing.lg, vertical = Spacing.sm)) {
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
            value = snapshot.cpuPercent?.let { stringResource(R.string.home_value_percent, it) }
                ?: stringResource(R.string.ssh_workspace_metric_unavailable),
            progress = snapshot.cpuPercent?.let { (it / 100.0).toFloat() },
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
