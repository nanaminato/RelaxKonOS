package app.relaxkonos.mobile.ui.home

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.Surface
import app.relaxkonos.mobile.ui.common.ApplicationTiles
import app.relaxkonos.mobile.ui.common.CatalogApplication
import app.relaxkonos.mobile.ui.common.mobileApplications
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.RecentOperation
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.ExecutionEligibilityNotice
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/** Server summary and capability-filtered application desktop, using only current-session facts. */
@Composable
fun HomeScreen(
    session: SessionState.Active,
    onOpenHelp: () -> Unit,
    modifier: Modifier = Modifier,
    onOpenRoute: (String) -> Unit = {},
    onSwitchLogin: () -> Unit = {},
    onOpenModule: (String) -> Unit = {},
) {
    val container = appContainer()
    val viewModel: HomeViewModel = viewModel(factory = viewModelFactory {
        initializer { HomeViewModel(container.session, container.system, container.recentOperations) { serviceId ->
            val tunnel = container.loginTunnels.all().firstOrNull { it.serviceId == serviceId }
            if (tunnel != null) "${tunnel.host}:${tunnel.port} → ${tunnel.remoteUrl}"
            else container.managedLogins.hostFor(serviceId)?.displayName
                ?: serviceId.takeIf { it.startsWith("http://") || it.startsWith("https://") }
        } }
    })
    HomeContent(viewModel, session, onOpenHelp, modifier,
        mobileApplications(session.capabilities, session.privilegedOperations,
            app.relaxkonos.mobile.ui.common.rememberServerModuleDirectory(session), onOpenModule, onOpenRoute), onSwitchLogin)
}

@Composable
internal fun HomeContent(
    viewModel: HomeViewModel,
    session: SessionState.Active,
    onOpenHelp: () -> Unit,
    modifier: Modifier = Modifier,
    applications: List<CatalogApplication> = emptyList(),
    onSwitchLogin: () -> Unit = {},
) {
    LaunchedEffect(session) { viewModel.refresh() }
    val snapshot = viewModel.snapshot
    val recentOperations = viewModel.recentOperations.collectAsStateValue()
    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(session.workspaceName, style = MaterialTheme.typography.headlineSmall)
                Text(session.userName, style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            FilledTonalIconButton(onClick = onSwitchLogin) {
                DesktopIcon(DesktopIcons.connections, size = 22.dp,
                    contentDescription = stringResource(R.string.desktop_switch_server))
            }
        }
        viewModel.message?.let { banner ->
            ActionFeedback(banner, onRetry = { viewModel.refresh() }, onDismiss = { viewModel.dismissMessage() })
        }
        Surface(shape = RoundedCornerShape(Radius.xl), color = MaterialTheme.colorScheme.secondaryContainer) {
            Column(Modifier.fillMaxWidth().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(stringResource(R.string.connections_active), style = MaterialTheme.typography.titleMedium)
                        Text(viewModel.connectionDescription(session.serviceId) ?: stringResource(R.string.home_connection_remote),
                            style = MaterialTheme.typography.bodySmall)
                    }
                    FilledTonalIconButton(onClick = { viewModel.refresh() }, enabled = !viewModel.loading && viewModel.metricsAvailable) {
                        DesktopIcon(DesktopIcons.refresh, size = 20.dp, contentDescription = stringResource(R.string.common_refresh))
                    }
                }
                when {
                    !viewModel.metricsAvailable -> Text(stringResource(R.string.home_metrics_absent), style = MaterialTheme.typography.bodySmall)
                    snapshot == null -> Text(stringResource(if (viewModel.loading) R.string.common_loading else R.string.home_metrics_empty), style = MaterialTheme.typography.bodySmall)
                    else -> {
                        if (snapshot.isStale) Text(stringResource(R.string.home_metrics_stale, formatTimestamp(snapshot.lastSampleMillis).orEmpty()),
                            style = MaterialTheme.typography.bodySmall)
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                            Column(Modifier.weight(1f)) {
                                Text(stringResource(R.string.home_label_cpu), style = MaterialTheme.typography.labelMedium)
                                Text(stringResource(R.string.home_value_percent, snapshot.cpuPercent), style = MaterialTheme.typography.titleMedium)
                            }
                            Column(Modifier.weight(1f)) {
                                Text(stringResource(R.string.home_label_memory), style = MaterialTheme.typography.labelMedium)
                                val memoryPercent = if (snapshot.memoryTotalBytes > 0) snapshot.memoryUsedBytes.toDouble() / snapshot.memoryTotalBytes * 100 else 0.0
                                Text(stringResource(R.string.home_value_percent, memoryPercent), style = MaterialTheme.typography.titleMedium)
                            }
                        }
                        snapshot.filesystems.firstOrNull()?.let { disk ->
                            Text(disk.id + " · " + stringResource(R.string.home_value_used_of_total,
                                formatSize(disk.usedBytes).orEmpty(), formatSize(disk.totalBytes).orEmpty()),
                                style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }
        }
        if (!session.executionEligibility.available) ExecutionEligibilityNotice(
            reason = session.executionEligibility.reason,
            privilegedFilesAvailable = session.executionEligibility.privilegedFilesAvailable)
        ApplicationTiles(applications)
        if (recentOperations.isNotEmpty()) {
            SectionCard(title = stringResource(R.string.home_recent_title), leading = DesktopIcons.history) {
                recentOperations.take(3).forEach { RecentOperationRow(it) }
            }
        }
        androidx.compose.material3.TextButton(onClick = onOpenHelp, modifier = Modifier.align(Alignment.End)) {
            Text(stringResource(R.string.help_title))
        }
    }
}

/** One recorded operation: what happened, where, and when. */
@Composable
private fun RecentOperationRow(operation: RecentOperation) {
    ListRow(
        title = recentOperationLabel(operation),
        subtitle = operation.target,
        supporting = formatTimestamp(operation.atEpochMillis),
        leading = { IconBadge(icon = recentOperationIcon(operation.kind)) },
    )
}

/**
 * The glyph for a recorded operation.
 *
 * These are the same marks the file screen offers the action with, so a recorded "rename" is
 * recognisable as the command the user ran.
 */
private fun recentOperationIcon(kind: RecentOperationKind): Int = when (kind) {
    RecentOperationKind.CreateDirectory -> DesktopIcons.newFolder
    RecentOperationKind.Rename -> DesktopIcons.rename
    RecentOperationKind.Delete -> DesktopIcons.delete
    RecentOperationKind.Copy -> DesktopIcons.copy
    RecentOperationKind.Move -> DesktopIcons.move
    RecentOperationKind.Upload -> DesktopIcons.upload
    RecentOperationKind.Download -> DesktopIcons.download
    RecentOperationKind.EndProcess -> DesktopIcons.delete
}

@Composable
private fun recentOperationLabel(operation: RecentOperation): String = stringResource(
    when (operation.kind) {
        RecentOperationKind.CreateDirectory -> R.string.home_recent_create_directory
        RecentOperationKind.Rename -> R.string.home_recent_rename
        RecentOperationKind.Delete -> R.string.home_recent_delete
        RecentOperationKind.Copy -> R.string.home_recent_copy
        RecentOperationKind.Move -> R.string.home_recent_move
        RecentOperationKind.Upload -> R.string.home_recent_upload
        RecentOperationKind.Download -> R.string.home_recent_download
        RecentOperationKind.EndProcess -> R.string.home_recent_end_process
    },
)
