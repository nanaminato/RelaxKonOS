package app.relaxkonos.mobile.ui.home

import android.app.Application
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
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
import app.relaxkonos.mobile.ui.common.DiskRow
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.MetricTile
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusChip
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.loadTone
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.relaxKon
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
 * The page leads with identity rather than with a list of facts: who this session is, on which host,
 * and whether that host is healthy — which is the question the screen exists to answer. Compact shows
 * the readings in one column; Medium and Expanded widen the same content
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

    LaunchedEffect(session.effectiveBaseUrl) { viewModel.refresh() }

    val snapshot = viewModel.snapshot
    val recentOperations = viewModel.recentOperations.collectAsStateValue()

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.nav_home),
            subtitle = session.workspaceName,
        )

        viewModel.message?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.refresh() },
                onDismiss = { viewModel.dismissMessage() },
            )
        }

        IdentityCard(session)

        SectionCard(
            title = stringResource(R.string.home_system_title),
            leading = DesktopIcons.system,
            trailing = {
                FilledTonalIconButton(
                    onClick = { viewModel.refresh() },
                    enabled = !viewModel.loading && viewModel.metricsAvailable,
                ) {
                    DesktopIcon(
                        icon = DesktopIcons.refresh,
                        size = 22.dp,
                        contentDescription = stringResource(R.string.common_refresh),
                    )
                }
            },
        ) {
            when {
                !viewModel.metricsAvailable -> EmptyHint(stringResource(R.string.home_metrics_absent))

                snapshot == null -> EmptyHint(
                    stringResource(if (viewModel.loading) R.string.common_loading else R.string.home_metrics_empty),
                )

                else -> {
                    if (snapshot.isStale) {
                        StatusChip(
                            text = stringResource(
                                R.string.home_metrics_stale,
                                formatTimestamp(snapshot.lastSampleMillis).orEmpty(),
                            ),
                            tone = StatusTone.Warning,
                        )
                    }
                    MetricsBlock(snapshot)
                }
            }
        }

        SectionCard(
            title = stringResource(R.string.home_capabilities_title),
            leading = DesktopIcons.capabilities,
            trailing = {
                if (session.capabilities.contains(ServerCapabilities.FILES)) {
                    Button(onClick = onOpenFiles) { Text(stringResource(R.string.home_open_files)) }
                }
            },
        ) {
            if (session.capabilities.isEmpty()) {
                EmptyHint(stringResource(R.string.home_capabilities_empty))
            } else {
                session.capabilities.sorted().forEach { capability ->
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                    ) {
                        // A success-toned dot rather than a checkmark: the desktop icon set has no
                        // tick, and inventing one here would put a second icon language in the row.
                        Box(
                            Modifier
                                .size(6.dp)
                                .background(MaterialTheme.relaxKon.success, CircleShape),
                        )
                        Text(
                            capability,
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
        }

        if (layoutState == LayoutState.Expanded) {
            SectionCard(
                title = stringResource(R.string.home_recent_title),
                leading = DesktopIcons.history,
            ) {
                if (recentOperations.isEmpty()) {
                    EmptyHint(stringResource(R.string.home_recent_empty))
                } else {
                    recentOperations.forEach { operation ->
                        RecentOperationRow(operation)
                    }
                }
            }
        }
    }
}

/**
 * The three readings, given equal weight.
 *
 * CPU and memory sit side by side because they are the two numbers a glance compares; a track under
 * each turns a percentage into "how close to the limit", which is what the raw value alone hides.
 */
@Composable
private fun MetricsBlock(snapshot: PerformanceSnapshot) {
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

    if (snapshot.filesystems.isNotEmpty()) {
        Text(stringResource(R.string.home_filesystems_title), style = MaterialTheme.typography.titleSmall)
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

/**
 * Who this session is.
 *
 * The gradient panel is the one piece of chrome the app allows itself. It answers "which host am I on"
 * before any reading, and the two chips below the name carry the state that is otherwise a sentence:
 * the connection is live, and this is the platform it is live on.
 *
 * The mark is the desktop's web-servers tile drawn bare rather than inside a badge: the artwork already
 * is a tile, and a second rounded square around it would read as two containers.
 */
@Composable
private fun IdentityCard(session: SessionState.Active) {
    val colors = MaterialTheme.relaxKon
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(Radius.xl))
            .background(Brush.linearGradient(listOf(colors.heroStart, colors.heroEnd)))
            .padding(Spacing.lg),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.md),
            ) {
                DesktopIcon(icon = DesktopIcons.host, size = 40.dp)
                Column(Modifier.weight(1f)) {
                    Text(
                        session.workspaceName,
                        style = MaterialTheme.typography.titleLarge,
                        color = colors.onHero,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        session.serviceId,
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.onHeroMuted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }

            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
            ) {
                HeroChip(stringResource(R.string.connections_active))
                HeroChip(session.serverPlatform)
            }

            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
            ) {
                DesktopIcon(icon = DesktopIcons.credentials, size = 16.dp)
                Text(
                    text = session.userName,
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.onHero,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

/** A translucent pill for the identity panel. [StatusChip] is for surfaces, not for a gradient. */
@Composable
private fun HeroChip(text: String) {
    val colors = MaterialTheme.relaxKon
    Box(
        modifier = Modifier
            .background(colors.onHero.copy(alpha = 0.18f), RoundedCornerShape(Radius.pill))
            .padding(horizontal = Spacing.md, vertical = Spacing.xs + 1.dp),
    ) {
        Text(text, style = MaterialTheme.typography.labelMedium, color = colors.onHero)
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
