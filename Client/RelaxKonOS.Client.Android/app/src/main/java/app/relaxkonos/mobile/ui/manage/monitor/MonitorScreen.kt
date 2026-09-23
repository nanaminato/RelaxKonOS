package app.relaxkonos.mobile.ui.manage.monitor

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.DiskRow
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.MetricTile
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusChip
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.loadTone
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Host performance.
 *
 * Only current values are shown. The gateway exposes a single snapshot
 * (`GET /api/v1.0/system/performance/snapshot`); the trend view the design sketches needs the metrics
 * stream, so instead of drawing a flat line the screen states that history is not available yet.
 *
 * The readings are grouped under "system status" rather than under the page's own name, so the header
 * says where you are and the card says what it holds — the previous version repeated the title twice.
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
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.manage_monitor_title),
            onBack = onBack,
        )

        viewModel.monitorMessage?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.loadMonitor() },
                onDismiss = { viewModel.dismissMonitorMessage() },
            )
        }

        SectionCard(
            title = stringResource(R.string.home_system_title),
            leadingPainter = painterResource(R.drawable.ic_activity),
            trailing = {
                FilledTonalIconButton(onClick = { viewModel.loadMonitor() }, enabled = !viewModel.monitorLoading) {
                    Icon(Icons.Filled.Refresh, contentDescription = stringResource(R.string.common_refresh))
                }
            },
        ) {
            when {
                !viewModel.metricsAvailable -> EmptyHint(stringResource(R.string.error_capability_missing))

                snapshot == null -> EmptyHint(
                    stringResource(if (viewModel.monitorLoading) R.string.common_loading else R.string.manage_monitor_empty),
                )

                else -> {
                    if (snapshot.isStale) {
                        StatusChip(
                            text = stringResource(
                                R.string.manage_monitor_stale,
                                formatTimestamp(snapshot.lastSampleMillis).orEmpty(),
                            ),
                            tone = StatusTone.Warning,
                        )
                    }

                    val cpuFraction = (snapshot.cpuPercent / 100.0).coerceIn(0.0, 1.0).toFloat()
                    val memoryFraction = if (snapshot.memoryTotalBytes > 0) {
                        (snapshot.memoryUsedBytes.toDouble() / snapshot.memoryTotalBytes).coerceIn(0.0, 1.0).toFloat()
                    } else {
                        0f
                    }

                    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                        MetricTile(
                            label = stringResource(R.string.home_label_cpu),
                            value = stringResource(R.string.home_value_percent, snapshot.cpuPercent),
                            progress = cpuFraction,
                            tone = loadTone(cpuFraction),
                            modifier = Modifier.weight(1f),
                        )
                        MetricTile(
                            label = stringResource(R.string.home_label_memory),
                            value = stringResource(R.string.home_value_percent, memoryFraction * 100.0),
                            supporting = stringResource(
                                R.string.home_value_used_of_total,
                                formatSize(snapshot.memoryUsedBytes).orEmpty(),
                                formatSize(snapshot.memoryTotalBytes).orEmpty(),
                            ),
                            progress = memoryFraction,
                            tone = loadTone(memoryFraction),
                            modifier = Modifier.weight(1f),
                        )
                    }

                    HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))

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
        }

        if (snapshot != null && snapshot.filesystems.isNotEmpty()) {
            SectionCard(
                title = stringResource(R.string.home_filesystems_title),
                leadingPainter = painterResource(R.drawable.ic_storage),
            ) {
                snapshot.filesystems.forEach { disk ->
                    DiskRow(
                        name = disk.id,
                        usedLabel = stringResource(
                            R.string.home_value_used_of_total,
                            formatSize(disk.usedBytes).orEmpty(),
                            formatSize(disk.totalBytes).orEmpty(),
                        ),
                        fraction = (disk.percent / 100.0).coerceIn(0.0, 1.0).toFloat(),
                    )
                }
            }
        }
    }
}
