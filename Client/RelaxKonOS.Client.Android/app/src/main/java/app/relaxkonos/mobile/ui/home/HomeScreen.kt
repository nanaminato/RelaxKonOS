package app.relaxkonos.mobile.ui.home

import android.app.Application
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
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.data.RecentOperation
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import kotlinx.coroutines.launch

/**
 * Home destination state.
 *
 * The snapshot is fetched through `SystemRepository`, which already owns the single-refresh retry, so
 * this holder only tracks what the screen must render. It keeps no host data beyond the last answer: a
 * stale snapshot rendered as fresh is worse than no snapshot.
 */
class HomeViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    var snapshot by mutableStateOf<PerformanceSnapshot?>(null)
        private set

    var message by mutableStateOf<UiMessage?>(null)
        private set

    var loading by mutableStateOf(false)
        private set

    val metricsAvailable: Boolean get() = container.capabilities.contains(ServerCapabilities.METRICS)

    val recentOperations get() = container.recentOperations.entries

    fun refresh() {
        if (!metricsAvailable || loading) {
            return
        }
        loading = true
        message = null
        viewModelScope.launch {
            when (val result = container.system.performance()) {
                is ApiResult.Success -> snapshot = result.value
                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    fun dismissMessage() {
        message = null
    }
}

/**
 * Home.
 *
 * Compact shows the status cards in one column; Medium and Expanded widen the same content
 * (`RelaxKonOS.Mobile.V1.Design.md` §3.2, `home` row). The recent-operations region exists only where
 * the design gives it a column and shows bounded, in-memory successes from this app session.
 */
@Composable
fun HomeScreen(
    session: SessionState.Active,
    layoutState: LayoutState,
    onOpenFiles: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: HomeViewModel = viewModel()

    LaunchedEffect(session.serverUrl) { viewModel.refresh() }

    val snapshot = viewModel.snapshot
    val recentOperations = viewModel.recentOperations.collectAsStateValue()

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(session.workspaceName, style = MaterialTheme.typography.headlineSmall)

        viewModel.message?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.refresh() },
                onDismiss = { viewModel.dismissMessage() },
            )
        }

        SectionCard(stringResource(R.string.home_session_title)) {
            KeyValueRow(stringResource(R.string.home_label_server), session.serverUrl)
            KeyValueRow(stringResource(R.string.home_label_user), session.userName)
            KeyValueRow(stringResource(R.string.home_label_workspace), session.workspaceName)
            KeyValueRow(stringResource(R.string.home_label_platform), session.serverPlatform)
        }

        SectionCard(
            title = stringResource(R.string.home_system_title),
            trailing = {
                Button(onClick = { viewModel.refresh() }, enabled = !viewModel.loading && viewModel.metricsAvailable) {
                    Text(stringResource(R.string.common_refresh))
                }
            },
        ) {
            if (!viewModel.metricsAvailable) {
                EmptyHint(stringResource(R.string.home_metrics_absent))
            } else if (snapshot == null) {
                EmptyHint(stringResource(if (viewModel.loading) R.string.common_loading else R.string.home_metrics_empty))
            } else {
                if (snapshot.isStale) {
                    Text(
                        stringResource(R.string.home_metrics_stale, formatTimestamp(snapshot.lastSampleMillis).orEmpty()),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                KeyValueRow(
                    stringResource(R.string.home_label_cpu),
                    stringResource(R.string.home_value_percent, snapshot.cpuPercent),
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
                LinearProgressIndicator(
                    progress = { (snapshot.cpuPercent / 100.0).coerceIn(0.0, 1.0).toFloat() },
                    modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
                )
                if (snapshot.filesystems.isNotEmpty()) {
                    Text(stringResource(R.string.home_filesystems_title), style = MaterialTheme.typography.titleSmall)
                    snapshot.filesystems.forEach { disk ->
                        KeyValueRow(
                            label = disk.id,
                            value = stringResource(
                                R.string.home_value_used_of_total,
                                formatSize(disk.usedBytes).orEmpty(),
                                formatSize(disk.totalBytes).orEmpty(),
                            ),
                        )
                    }
                }
            }
        }

        SectionCard(title = stringResource(R.string.home_capabilities_title)) {
            if (session.capabilities.isEmpty()) {
                EmptyHint(stringResource(R.string.home_capabilities_empty))
            } else {
                session.capabilities.sorted().forEach { capability ->
                    Text(
                        capability,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (session.capabilities.contains(ServerCapabilities.FILES)) {
                Button(onClick = onOpenFiles) { Text(stringResource(R.string.home_open_files)) }
            }
        }

        if (layoutState == LayoutState.Expanded) {
            SectionCard(title = stringResource(R.string.home_recent_title)) {
                if (recentOperations.isEmpty()) {
                    EmptyHint(stringResource(R.string.home_recent_empty))
                } else {
                    recentOperations.forEach { operation ->
                        Text(
                            text = stringResource(
                                R.string.home_recent_item,
                                recentOperationLabel(operation),
                                operation.target,
                                formatTimestamp(operation.atEpochMillis).orEmpty(),
                            ),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }
        }
    }
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
