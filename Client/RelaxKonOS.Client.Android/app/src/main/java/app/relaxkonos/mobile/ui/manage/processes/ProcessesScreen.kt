package app.relaxkonos.mobile.ui.manage.processes

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Process list.
 *
 * Paged because the server paginates, and sorted by CPU on the server side so the client cannot
 * disagree with it about ordering. Ending a process is a dangerous operation, so it goes through the
 * naming confirmation and then the elevation dialog.
 *
 * The rows live in a [SectionGroup] rather than loose on the backdrop: a process list is a set of
 * interchangeable entries, and a shared container is what says so.
 */
@Composable
fun ProcessesScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val viewModel: ManageViewModel = viewModel()

    LaunchedEffect(Unit) { viewModel.loadProcesses(1) }

    var forceKill by remember { mutableStateOf(false) }

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.manage_processes_title),
            onBack = onBack,
        )

        viewModel.processMessage?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.loadProcesses() },
                onDismiss = { viewModel.dismissProcessMessage() },
            )
        }

        if (!viewModel.processesAvailable) {
            EmptyState(
                text = stringResource(R.string.error_capability_missing),
                icon = Icons.Filled.Build,
            )
            return@Column
        }

        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            OutlinedTextField(
                value = viewModel.processFilter,
                onValueChange = { viewModel.updateProcessFilter(it) },
                modifier = Modifier.weight(1f),
                singleLine = true,
                label = { Text(stringResource(R.string.manage_processes_filter)) },
                leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
                shape = MaterialTheme.shapes.medium,
            )
            Button(onClick = { viewModel.loadProcesses(1) }, enabled = !viewModel.processesLoading) {
                Text(stringResource(R.string.common_search))
            }
        }

        if (viewModel.processItems.isEmpty()) {
            EmptyState(
                text = stringResource(
                    if (viewModel.processesLoading) R.string.common_loading else R.string.manage_processes_empty,
                ),
                icon = Icons.AutoMirrored.Filled.List,
            )
        } else {
            SectionGroup(modifier = Modifier.weight(1f)) {
                LazyColumn(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    items(viewModel.processItems, key = { it.pid }) { process ->
                        ListRow(
                            title = process.name,
                            subtitle = stringResource(R.string.manage_processes_pid, process.pid),
                            supporting = listOfNotNull(
                                stringResource(R.string.manage_processes_cpu, process.cpuPercent),
                                formatSize(process.memoryBytes),
                                process.userName,
                            ).joinToString(" · "),
                            leading = { IconBadge(icon = painterResource(R.drawable.ic_process)) },
                            trailing = {
                                TextButton(
                                    onClick = { viewModel.requestKill(process) },
                                    colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
                                ) { Text(stringResource(R.string.manage_processes_kill)) }
                            },
                        )
                    }
                }
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                TextButton(
                    onClick = { viewModel.loadProcesses(viewModel.processPage - 1) },
                    enabled = viewModel.processPage > 1 && !viewModel.processesLoading,
                ) { Text(stringResource(R.string.manage_processes_previous)) }
                Text(
                    stringResource(
                        R.string.manage_processes_page,
                        viewModel.processPage,
                        viewModel.processTotalCount.coerceAtLeast(viewModel.processItems.size),
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                TextButton(
                    onClick = { viewModel.loadProcesses(viewModel.processPage + 1) },
                    enabled = viewModel.hasNextPage && !viewModel.processesLoading,
                ) { Text(stringResource(R.string.manage_processes_next)) }
            }
        }
    }

    viewModel.killTarget?.let { process ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.manage_processes_kill_title),
            message = stringResource(R.string.manage_processes_kill_message, process.name, process.pid),
            confirmLabel = stringResource(R.string.manage_processes_kill),
            busy = viewModel.processesLoading,
            onConfirm = { viewModel.confirmKill(forceKill) },
            onDismiss = {
                forceKill = false
                viewModel.cancelKill()
            },
            extraContent = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = forceKill, onCheckedChange = { forceKill = it })
                    Text(stringResource(R.string.manage_processes_kill_force))
                }
            },
        )
    }
}
