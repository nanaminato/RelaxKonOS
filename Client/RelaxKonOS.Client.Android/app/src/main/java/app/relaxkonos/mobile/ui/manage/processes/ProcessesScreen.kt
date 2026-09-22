package app.relaxkonos.mobile.ui.manage.processes

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.manage.ManageViewModel

/**
 * Process list.
 *
 * Paged because the server paginates, and sorted by CPU on the server side so the client cannot
 * disagree with it about ordering. Ending a process is a dangerous operation, so it goes through the
 * naming confirmation and then the elevation dialog.
 */
@Composable
fun ProcessesScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val viewModel: ManageViewModel = viewModel()

    LaunchedEffect(Unit) { viewModel.loadProcesses(1) }

    var forceKill by remember { mutableStateOf(false) }

    Column(modifier = modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }

        viewModel.processMessage?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.loadProcesses() },
                onDismiss = { viewModel.dismissProcessMessage() },
            )
        }

        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = viewModel.processFilter,
                onValueChange = { viewModel.updateProcessFilter(it) },
                modifier = Modifier.weight(1f),
                singleLine = true,
                label = { Text(stringResource(R.string.manage_processes_filter)) },
            )
            Button(onClick = { viewModel.loadProcesses(1) }, enabled = !viewModel.processesLoading) {
                Text(stringResource(R.string.common_search))
            }
        }

        if (!viewModel.processesAvailable) {
            EmptyHint(stringResource(R.string.error_capability_missing))
        } else if (viewModel.processItems.isEmpty()) {
            EmptyHint(
                stringResource(if (viewModel.processesLoading) R.string.common_loading else R.string.manage_processes_empty),
            )
        } else {
            LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                items(viewModel.processItems, key = { it.pid }) { process ->
                    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f).padding(vertical = 6.dp)) {
                            Text(
                                text = process.name,
                                style = MaterialTheme.typography.bodyLarge,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = listOfNotNull(
                                    stringResource(R.string.manage_processes_pid, process.pid),
                                    stringResource(R.string.manage_processes_cpu, process.cpuPercent),
                                    formatSize(process.memoryBytes),
                                    process.userName,
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        TextButton(onClick = { viewModel.requestKill(process) }) {
                            Text(stringResource(R.string.manage_processes_kill))
                        }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
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
