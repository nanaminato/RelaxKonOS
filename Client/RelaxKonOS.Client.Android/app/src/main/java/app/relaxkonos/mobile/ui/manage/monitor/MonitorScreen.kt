package app.relaxkonos.mobile.ui.manage.monitor

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.manage.ManageViewModel

/**
 * Host performance.
 *
 * Only current values are shown. The gateway exposes a single snapshot
 * (`GET /api/v1.0/system/performance/snapshot`); the trend view the design sketches needs the metrics
 * stream, so instead of drawing a flat line the screen states that history is not available yet.
 */
@Composable
fun MonitorScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val viewModel: ManageViewModel = viewModel()

    LaunchedEffect(Unit) { viewModel.loadMonitor() }

    val snapshot = viewModel.snapshot

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }

        viewModel.monitorMessage?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.loadMonitor() },
                onDismiss = { viewModel.dismissMonitorMessage() },
            )
        }

        SectionCard(
            title = stringResource(R.string.manage_monitor_title),
            trailing = {
                Button(onClick = { viewModel.loadMonitor() }, enabled = !viewModel.monitorLoading) {
                    Text(stringResource(R.string.common_refresh))
                }
            },
        ) {
            if (!viewModel.metricsAvailable) {
                EmptyHint(stringResource(R.string.error_capability_missing))
            } else if (snapshot == null) {
                EmptyHint(
                    stringResource(if (viewModel.monitorLoading) R.string.common_loading else R.string.manage_monitor_empty),
                )
            } else {
                if (snapshot.isStale) {
                    Text(
                        stringResource(R.string.manage_monitor_stale, formatTimestamp(snapshot.lastSampleMillis).orEmpty()),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                KeyValueRow(
                    stringResource(R.string.home_label_cpu),
                    stringResource(R.string.home_value_percent, snapshot.cpuPercent),
                )
                LinearProgressIndicator(
                    progress = { (snapshot.cpuPercent / 100.0).coerceIn(0.0, 1.0).toFloat() },
                    modifier = Modifier.fillMaxWidth(),
                )
                KeyValueRow(
                    stringResource(R.string.home_label_memory),
                    stringResource(
                        R.string.home_value_used_of_total,
                        formatSize(snapshot.memoryUsedBytes).orEmpty(),
                        formatSize(snapshot.memoryTotalBytes).orEmpty(),
                    ),
                )
                KeyValueRow(
                    stringResource(R.string.home_label_uptime),
                    stringResource(R.string.home_value_seconds, snapshot.uptimeSeconds),
                )
                Text(
                    stringResource(R.string.manage_monitor_no_history),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        if (snapshot != null && snapshot.filesystems.isNotEmpty()) {
            SectionCard(stringResource(R.string.home_filesystems_title)) {
                snapshot.filesystems.forEach { disk ->
                    KeyValueRow(
                        label = disk.id,
                        value = stringResource(
                            R.string.home_value_used_of_total,
                            formatSize(disk.usedBytes).orEmpty(),
                            formatSize(disk.totalBytes).orEmpty(),
                        ),
                    )
                    LinearProgressIndicator(
                        progress = { (disk.percent / 100.0).coerceIn(0.0, 1.0).toFloat() },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
        }
    }
}
