package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.MetricTile
import app.relaxkonos.mobile.ui.common.SectionCard
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

/** Fixed, read-only Linux host inspection.  The UI never accepts a command string from the user. */
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
                    val result = session.sshTransport.run(SYSTEM_SNAPSHOT_COMMAND)
                    if (!result.succeeded) null else parseSnapshot(result.standardOutput)
                }
                mutableState.update { it.copy(snapshot = snapshot, loading = false, problem = snapshot == null) }
            } catch (_: Exception) {
                mutableState.update { it.copy(loading = false, problem = true) }
            } finally {
                secret.fill('\u0000')
            }
        }
    }

    private fun parseSnapshot(output: String): SshSystemSnapshot? {
        val values = output.lineSequence()
            .mapNotNull { line -> line.split('=', limit = 2).takeIf { it.size == 2 } }
            .associate { (key, value) -> key to value }
        val cpu = values["cpu"]?.toDoubleOrNull()?.coerceIn(0.0, 100.0) ?: return null
        val total = values["memoryTotal"]?.toLongOrNull()?.takeIf { it > 0 } ?: return null
        val available = values["memoryAvailable"]?.toLongOrNull()?.coerceIn(0L, total) ?: return null
        val uptime = values["uptime"]?.toLongOrNull()?.coerceAtLeast(0L) ?: return null
        return SshSystemSnapshot(cpu, total - available, total, uptime, values["system"].orEmpty())
    }

    private companion object {
        val SYSTEM_SNAPSHOT_COMMAND = """
            sh -c 'read _ u n s i w x y z _ &lt; /proc/stat
            t1=${'$'}((u+n+s+i+w+x+y+z)); idle1=${'$'}((i+w)); sleep 1
            read _ u n s i w x y z _ &lt; /proc/stat
            t2=${'$'}((u+n+s+i+w+x+y+z)); idle2=${'$'}((i+w)); delta=${'$'}((t2-t1))
            [ "${'$'}delta" -gt 0 ] || delta=1; cpu=${'$'}((100*(delta-(idle2-idle1))/delta))
            total=${'$'}(awk "/^MemTotal:/ {print ${'$'}2 * 1024}" /proc/meminfo)
            available=${'$'}(awk "/^MemAvailable:/ {print ${'$'}2 * 1024}" /proc/meminfo)
            uptime=${'$'}(cut -d. -f1 /proc/uptime)
            printf "cpu=%s\\nmemoryTotal=%s\\nmemoryAvailable=%s\\nuptime=%s\\nsystem=%s\\n" "${'$'}cpu" "${'$'}total" "${'$'}available" "${'$'}uptime" "${'$'}(uname -sr)"'
        """.trimIndent().replace("&lt;", "<")
    }
}

data class SshSystemUiState(
    val loading: Boolean = false,
    val snapshot: SshSystemSnapshot? = null,
    val problem: Boolean = false,
)

data class SshSystemSnapshot(
    val cpuPercent: Double,
    val memoryUsedBytes: Long,
    val memoryTotalBytes: Long,
    val uptimeSeconds: Long,
    val system: String,
)

@Composable
fun SshSystemScreen(hostId: String, onExit: () -> Unit, modifier: Modifier = Modifier) {
    val model: SshSystemViewModel = viewModel()
    val state by model.state.collectAsState()
    LaunchedEffect(hostId) { model.refresh(hostId) }
    val snapshot = state.snapshot
    Column(
        modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        SectionCard(
            title = stringResource(R.string.ssh_workspace_system),
            subtitle = stringResource(R.string.ssh_workspace_system_subtitle, hostId),
            leading = DesktopIcons.system,
            trailing = {
                IconButton(onClick = { model.refresh(hostId) }, enabled = !state.loading) {
                    DesktopIcon(DesktopIcons.refresh, contentDescription = stringResource(R.string.common_refresh))
                }
            },
        ) {
            when {
                state.loading && snapshot == null -> CircularProgressIndicator()
                snapshot != null -> SystemMetrics(snapshot)
                state.problem -> Text(stringResource(R.string.ssh_workspace_system_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        OutlinedButton(onClick = onExit, modifier = Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.ssh_workspace_exit))
        }
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
    KeyValueRow(stringResource(R.string.home_label_uptime), formatUptime(snapshot.uptimeSeconds))
    if (snapshot.system.isNotBlank()) KeyValueRow(stringResource(R.string.ssh_workspace_system_platform), snapshot.system)
}
