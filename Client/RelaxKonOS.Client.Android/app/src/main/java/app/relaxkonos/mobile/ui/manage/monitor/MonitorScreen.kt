package app.relaxkonos.mobile.ui.manage.monitor

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.awaitCancellation

@Composable
fun MonitorScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier, active: Boolean = true) {
    val model: MonitorViewModel = viewModel()
    val state by model.state.collectAsStateWithLifecycle()
    val auth by appContainer().session.state.collectAsStateWithLifecycle()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(active, lifecycle, auth, model.available) {
        if (!active || !model.available) return@LaunchedEffect
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            try { model.observe(); awaitCancellation() } finally { model.stopObserving() }
        }
    }
    DisposableEffect(model) { onDispose { model.stopObserving() } }
    val resources = remember(state.info, state.snapshot) { performanceResources(state.info, state.snapshot) }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.manage_monitor_title), onBack = if (model.selected != null) ({ model.select(null) }) else onBack,
            trailing = { FilledTonalIconButton(onClick = model::retry, enabled = state.phase != PerformancePhase.Connecting) {
                DesktopIcon(DesktopIcons.refresh, size = 22.dp, contentDescription = stringResource(R.string.common_refresh))
            } })
        StatusChip(stringResource(when (state.phase) {
            PerformancePhase.Live -> R.string.monitor_live
            PerformancePhase.Connecting -> R.string.monitor_connecting
            PerformancePhase.Snapshot -> R.string.monitor_snapshot_mode
            PerformancePhase.Failed -> R.string.monitor_unavailable
            PerformancePhase.Idle -> R.string.common_loading
        }), tone = if (state.phase == PerformancePhase.Live && state.snapshot?.health?.isStale != true) StatusTone.Success else StatusTone.Warning)
        state.snapshot?.let { sample ->
            Text(stringResource(R.string.monitor_sample_time, formatTimestamp(IsoInstant.toEpochMillis(sample.timestamp)).orEmpty()), style = MaterialTheme.typography.bodySmall)
            if (sample.health.isStale || sample.health.error != null) Text(stringResource(R.string.monitor_sample_unhealthy), color = MaterialTheme.colorScheme.error)
        }
        state.problem?.failureMessage()?.let { problem -> ActionFeedback(problem.text(), onRetry = model::retry, onDismiss = model::dismissProblem) }
        if (!model.available) { EmptyHint(stringResource(R.string.error_capability_missing)); return@Column }
        if (resources.isEmpty()) { EmptyHint(stringResource(if (state.phase == PerformancePhase.Connecting) R.string.common_loading else R.string.manage_monitor_empty)); return@Column }
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            val split = monitorUsesTwoPanes(maxWidth.value, maxHeight.value, LocalDensity.current.fontScale)
            BackHandler(enabled = active && !split && model.selected != null) { model.select(null) }
            val selected = model.selected?.let { key -> resources.firstOrNull { it.key == key.key } } ?: if (split) resources.firstOrNull() else null
            if (split) {
                Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                    ResourceList(resources, selected, state, model::select, Modifier.width((280 * LocalDensity.current.fontScale).coerceAtMost(400f).dp))
                    selected?.let { PerformanceDetails(it, state, Modifier.weight(1f)) }
                }
            } else if (selected != null) PerformanceDetails(selected, state, Modifier.fillMaxSize())
            else ResourceList(resources, null, state, model::select, Modifier.fillMaxSize())
        }
    }
}

@Composable
private fun resourceTitle(resource: PerformanceResource): String = resource.name ?: stringResource(when (resource.kind) {
    PerformanceKind.Cpu -> R.string.home_label_cpu
    PerformanceKind.Memory -> R.string.home_label_memory
    PerformanceKind.Filesystem -> R.string.monitor_filesystem
    PerformanceKind.Disk -> R.string.monitor_disk
    PerformanceKind.Network -> R.string.monitor_network
})

@Composable
private fun ResourceList(resources: List<PerformanceResource>, selected: PerformanceResource?, state: PerformanceState,
    select: (PerformanceResource) -> Unit, modifier: Modifier) {
    LazyColumn(modifier, verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        items(resources, key = { it.key }) { resource ->
            OutlinedButton(onClick = { select(resource) }, modifier = Modifier.fillMaxWidth().heightIn(min = 64.dp),
                colors = ButtonDefaults.outlinedButtonColors(containerColor = if (selected?.key == resource.key) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface)) {
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    Text(resourceTitle(resource), style = MaterialTheme.typography.titleSmall)
                    val value = when (resource.kind) {
                        PerformanceKind.Cpu -> percent(state.snapshot?.cpu?.totalPercent)
                        PerformanceKind.Memory -> bytes(state.snapshot?.memory?.usedBytes)
                        PerformanceKind.Filesystem -> percent(state.snapshot?.filesystems?.firstOrNull { it.id == resource.id }?.percent)
                        PerformanceKind.Disk -> rate(state.snapshot?.disks?.firstOrNull { it.id == resource.id }?.takeIf { state.info?.capabilities?.diskIo != false }?.readBytesPerSecond)
                        PerformanceKind.Network -> rate(state.snapshot?.networks?.firstOrNull { it.id == resource.id }?.receiveBytesPerSecond)
                    }
                    Text(value, style = MaterialTheme.typography.bodyMedium)
                }
            }
        }
    }
}

@Composable
private fun PerformanceDetails(resource: PerformanceResource, state: PerformanceState, modifier: Modifier) {
    val info = state.info; val snapshot = state.snapshot
    val rows = metricRows(resource, state)
    val primary: (PerformanceSnapshot) -> Double? = when (resource.kind) {
        PerformanceKind.Cpu -> { sample -> sample.cpu.totalPercent }
        PerformanceKind.Memory -> { sample -> memoryPercent(sample.memory) }
        PerformanceKind.Filesystem -> { sample -> sample.filesystems.firstOrNull { it.id == resource.id }?.percent }
        PerformanceKind.Disk -> { sample -> if (info?.capabilities?.diskIo == false) null else sample.disks.firstOrNull { it.id == resource.id }?.readBytesPerSecond?.toDouble() }
        PerformanceKind.Network -> { sample -> sample.networks.firstOrNull { it.id == resource.id }?.receiveBytesPerSecond?.toDouble() }
    }
    val secondary: ((PerformanceSnapshot) -> Double?)? = when (resource.kind) {
        PerformanceKind.Disk -> { sample -> if (info?.capabilities?.diskIo == false) null else sample.disks.firstOrNull { it.id == resource.id }?.writeBytesPerSecond?.toDouble() }
        PerformanceKind.Network -> { sample -> sample.networks.firstOrNull { it.id == resource.id }?.sendBytesPerSecond?.toDouble() }
        else -> null
    }
    val rateChart = resource.kind == PerformanceKind.Disk || resource.kind == PerformanceKind.Network
    val firstLabel = stringResource(when (resource.kind) { PerformanceKind.Disk -> R.string.monitor_read; PerformanceKind.Network -> R.string.monitor_receive; else -> R.string.monitor_utilization })
    val secondLabel = if (secondary != null) stringResource(if (resource.kind == PerformanceKind.Disk) R.string.monitor_write else R.string.monitor_send) else null
    LazyColumn(modifier, verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        item { Text(resourceTitle(resource), style = MaterialTheme.typography.titleLarge) }
        item { TrendChart(state.history, primary, secondary, firstLabel, secondLabel, rateChart) }
        item { SectionCard(title = stringResource(R.string.monitor_details), leading = DesktopIcons.system) {
            rows.forEach { (label, value) -> MetricRow(stringResource(label), value) }
        } }
        if (resource.kind == PerformanceKind.Cpu) {
            if (info?.capabilities?.perLogicalCpu == false || snapshot?.cpu?.perLogicalCpuPercent.isNullOrEmpty()) {
                item { EmptyHint(stringResource(R.string.monitor_per_cpu_unavailable)) }
            } else {
                item { Text(stringResource(R.string.monitor_per_cpu), style = MaterialTheme.typography.titleMedium) }
                items(snapshot!!.cpu.perLogicalCpuPercent.size) { index ->
                    val value = snapshot.cpu.perLogicalCpuPercent[index]
                    Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                        MetricRow(stringResource(R.string.monitor_cpu_index, index), percent(value))
                        LinearProgressIndicator(progress = { (value / 100).toFloat() }, modifier = Modifier.fillMaxWidth())
                    }
                }
            }
        }
        if (resource.kind == PerformanceKind.Network) {
            val network = info?.networks?.firstOrNull { it.id == resource.id }
            val addresses = network?.addresses.orEmpty() + state.addresses.filter { it.interfaceName == network?.name }.map { it.address }
            item { SectionCard(title = stringResource(R.string.monitor_addresses), leading = DesktopIcons.system) {
                if (addresses.isEmpty()) EmptyHint(stringResource(R.string.monitor_unknown))
                else addresses.distinct().forEach { address -> SelectionContainer { Text(address) } }
            } }
        }
    }
}

@Composable
private fun metricRows(resource: PerformanceResource, state: PerformanceState): List<Pair<Int, String>> {
    val info = state.info; val snapshot = state.snapshot
    return when (resource.kind) {
        PerformanceKind.Cpu -> {
            val cpu = snapshot?.cpu; val hardware = info?.cpu; val capabilities = info?.capabilities
            listOf(
                R.string.monitor_model to (hardware?.model ?: unknown()),
                R.string.monitor_utilization to percent(cpu?.totalPercent),
                R.string.monitor_cpu_user to percent(cpu?.userPercent), R.string.monitor_cpu_system to percent(cpu?.systemPercent),
                R.string.monitor_cpu_idle to percent(cpu?.idlePercent), R.string.monitor_iowait to percent(cpu?.iowaitPercent?.takeIf { capabilities?.cpuIowait != false }),
                R.string.monitor_frequency to frequency(cpu?.currentFrequencyMHz?.takeIf { capabilities?.cpuFrequency != false }),
                R.string.monitor_base_frequency to frequency(hardware?.baseFrequencyMHz),
                R.string.monitor_process_count to count(cpu?.processCount?.toLong()), R.string.monitor_thread_count to count(cpu?.threadCount?.toLong()),
                R.string.monitor_handles to count(cpu?.handleCount), R.string.home_label_uptime to (snapshot?.let { formatUptime(it.uptimeSeconds) } ?: unknown()),
                R.string.monitor_sockets to count(hardware?.socketCount?.toLong()), R.string.monitor_cores to count(hardware?.physicalCoreCount?.toLong()),
                R.string.monitor_logical_cpu to count(hardware?.logicalProcessorCount?.toLong()),
                R.string.monitor_virtualization to (hardware?.virtualizationEnabled?.let { stringResource(if (it) R.string.monitor_enabled else R.string.monitor_disabled) } ?: unknown()),
                R.string.monitor_l1 to bytes(hardware?.l1CacheBytes), R.string.monitor_l2 to bytes(hardware?.l2CacheBytes), R.string.monitor_l3 to bytes(hardware?.l3CacheBytes),
            )
        }
        PerformanceKind.Memory -> {
            val memory = snapshot?.memory
            listOf(R.string.monitor_utilization to percent(memory?.let(::memoryPercent)),
                R.string.monitor_total to bytes(memory?.totalBytes ?: info?.memory?.totalBytes), R.string.monitor_used to bytes(memory?.usedBytes),
                R.string.monitor_available to bytes(memory?.availableBytes), R.string.monitor_cached to bytes(memory?.cachedBytes),
                R.string.monitor_buffered to bytes(memory?.bufferedBytes), R.string.monitor_swap_used to bytes(memory?.swapUsedBytes),
                R.string.monitor_swap_total to bytes(memory?.swapTotalBytes ?: info?.memory?.swapTotalBytes))
        }
        PerformanceKind.Filesystem -> {
            val identity = info?.filesystems?.firstOrNull { it.id == resource.id }; val current = snapshot?.filesystems?.firstOrNull { it.id == resource.id }
            listOf(R.string.monitor_mount to (identity?.mountPoint ?: unknown()), R.string.monitor_total to bytes(current?.totalBytes),
                R.string.monitor_used to bytes(current?.usedBytes), R.string.monitor_available to bytes(current?.availableBytes), R.string.monitor_utilization to percent(current?.percent))
        }
        PerformanceKind.Disk -> {
            val identity = info?.disks?.firstOrNull { it.id == resource.id }
            val current = snapshot?.disks?.firstOrNull { it.id == resource.id }?.takeIf { info?.capabilities?.diskIo != false }
            val mounts = identity?.filesystemIds.orEmpty().mapNotNull { id -> info?.filesystems?.firstOrNull { it.id == id }?.mountPoint }
            listOf(R.string.monitor_model to (identity?.model ?: unknown()), R.string.monitor_mount to mounts.joinToString("\n").ifEmpty { unknown() },
                R.string.monitor_read to rate(current?.readBytesPerSecond), R.string.monitor_write to rate(current?.writeBytesPerSecond),
                R.string.monitor_read_iops to number(current?.readIops), R.string.monitor_write_iops to number(current?.writeIops),
                R.string.monitor_activity to percent(current?.activityPercent),
                R.string.monitor_queue to number(current?.queueLength?.takeIf { info?.capabilities?.diskQueueLength != false }),
                R.string.monitor_latency to (current?.latencyMs?.takeIf { info?.capabilities?.diskLatency != false }?.let { stringResource(R.string.monitor_milliseconds, it) } ?: unknown()))
        }
        PerformanceKind.Network -> {
            val identity = info?.networks?.firstOrNull { it.id == resource.id }; val current = snapshot?.networks?.firstOrNull { it.id == resource.id }
            val errors = info?.capabilities?.networkErrors != false
            listOf(R.string.monitor_link_speed to (identity?.linkSpeedBitsPerSecond?.let { stringResource(R.string.monitor_mbps, it / 1_000_000.0) } ?: unknown()),
                R.string.monitor_receive to rate(current?.receiveBytesPerSecond), R.string.monitor_send to rate(current?.sendBytesPerSecond),
                R.string.monitor_bytes_received to bytes(current?.bytesReceived), R.string.monitor_bytes_sent to bytes(current?.bytesSent),
                R.string.monitor_packets_received to count(current?.receivePackets), R.string.monitor_packets_sent to count(current?.sendPackets),
                R.string.monitor_receive_errors to count(current?.receiveErrors?.takeIf { errors }), R.string.monitor_send_errors to count(current?.sendErrors?.takeIf { errors }),
                R.string.monitor_receive_dropped to count(current?.receiveDropped?.takeIf { errors }), R.string.monitor_send_dropped to count(current?.sendDropped?.takeIf { errors }))
        }
    }
}

private fun memoryPercent(memory: MemoryRealtimeMetrics): Double? = if (memory.totalBytes > 0) memory.usedBytes.toDouble() / memory.totalBytes * 100 else null
@Composable private fun unknown() = stringResource(R.string.monitor_unknown)
@Composable private fun bytes(value: Long?) = formatSize(value) ?: unknown()
@Composable private fun percent(value: Double?) = value?.let { stringResource(R.string.home_value_percent, it) } ?: unknown()
@Composable private fun number(value: Double?) = value?.let { stringResource(R.string.monitor_number, it) } ?: unknown()
@Composable private fun count(value: Long?) = value?.let { stringResource(R.string.monitor_count, it) } ?: unknown()
@Composable private fun rate(value: Long?) = value?.let { stringResource(R.string.monitor_rate, bytes(it)) } ?: unknown()
@Composable private fun frequency(value: Double?) = value?.let { stringResource(R.string.monitor_mhz, it) } ?: unknown()
@Composable private fun MetricRow(label: String, value: String) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Text(label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        SelectionContainer { Text(value, style = MaterialTheme.typography.bodyLarge) }
    }
}

@Composable
private fun TrendChart(history: List<PerformanceSnapshot>, primary: (PerformanceSnapshot) -> Double?, secondary: ((PerformanceSnapshot) -> Double?)?,
    firstLabel: String, secondLabel: String?, rateChart: Boolean) {
    val first = trendPoints(history, primary); val second = secondary?.let { trendPoints(history, it) }.orEmpty()
    val values = (first + second).mapNotNull { it.value }
    val ceiling = if (rateChart) maxOf(1.0, values.maxOrNull() ?: 1.0) else 100.0
    val firstColor = MaterialTheme.colorScheme.primary; val secondColor = MaterialTheme.colorScheme.tertiary
    val grid = MaterialTheme.colorScheme.outlineVariant
    val description = stringResource(R.string.monitor_trend_description, history.size)
    SectionCard(title = stringResource(R.string.monitor_trend), leading = DesktopIcons.system) {
        Text(firstLabel, color = firstColor)
        if (secondLabel != null) Text(secondLabel, color = secondColor)
        if (values.isEmpty()) EmptyHint(stringResource(R.string.monitor_trend_empty))
        else {
            Text(if (rateChart) rate(ceiling.toLong()) else percent(ceiling), style = MaterialTheme.typography.labelSmall)
            Canvas(Modifier.fillMaxWidth().height(140.dp).semantics { contentDescription = description }) {
                repeat(5) { level -> val y = size.height * level / 4; drawLine(grid, Offset(0f, y), Offset(size.width, y)) }
                val latest = (first + second).maxOfOrNull { it.timeMillis } ?: return@Canvas
                fun point(value: TrendPoint) = Offset(((value.timeMillis - (latest - 60_000)) / 60_000.0 * size.width).toFloat().coerceIn(0f, size.width),
                    (size.height * (1 - (value.value!! / ceiling).coerceIn(0.0, 1.0))).toFloat())
                fun series(values: List<TrendPoint>, color: androidx.compose.ui.graphics.Color) {
                    trendSegments(values).forEach { segment ->
                        val path = Path(); segment.forEachIndexed { index, value -> val at = point(value); if (index == 0) path.moveTo(at.x, at.y) else path.lineTo(at.x, at.y); drawCircle(color, 2.dp.toPx(), at) }
                        drawPath(path, color, style = Stroke(2.dp.toPx()))
                    }
                }
                series(first, firstColor); series(second, secondColor)
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(stringResource(R.string.monitor_sixty_seconds), style = MaterialTheme.typography.labelSmall)
                Text(stringResource(R.string.monitor_latest), style = MaterialTheme.typography.labelSmall)
            }
            Text(stringResource(R.string.monitor_trend_description, history.size), style = MaterialTheme.typography.bodySmall)
        }
    }
}
