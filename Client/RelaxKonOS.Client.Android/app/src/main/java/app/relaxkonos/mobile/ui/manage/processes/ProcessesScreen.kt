package app.relaxkonos.mobile.ui.manage.processes

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.ui.platform.LocalDensity
import app.relaxkonos.mobile.core.net.RemoteProcess
import app.relaxkonos.mobile.ui.manage.monitor.monitorUsesTwoPanes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import app.relaxkonos.mobile.core.net.ProcessSort
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.core.net.IsoInstant
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.theme.Spacing


/**
 * Process list.
 *
 * Paging, filtering and the selected ordering are applied by the server. Ending a process is a dangerous operation, so it goes through the
 * confirmation of the original instance; host OS permissions remain authoritative.
 *
 * The rows live in a [SectionGroup] rather than loose on the backdrop: a process list is a set of
 * interchangeable entries, and a shared container is what says so.
 */
@Composable
fun ProcessesScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
    active: Boolean = true,
) {
    val viewModel: ManageViewModel = viewModel()

    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val auth by appContainer().session.state.collectAsStateWithLifecycle()
    LaunchedEffect(active, lifecycle, auth, viewModel.processesAvailable) {
        if (!active || !viewModel.processesAvailable) return@LaunchedEffect
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            viewModel.observeProcesses()
        }
    }
    DisposableEffect(viewModel) { onDispose { viewModel.stopProcessObserving() } }
    var sorting by remember { mutableStateOf(false) }
    val keyboard = LocalSoftwareKeyboardController.current

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.manage_processes_title),
            onBack = if (viewModel.processSelected != null) ({ viewModel.selectProcess(null) }) else onBack,
        )

        viewModel.processMessage?.let { banner ->
            ActionFeedback(
                message = banner,
                onRetry = { viewModel.loadProcesses() },
                onDismiss = { viewModel.dismissProcessMessage() },
            )
        }

        viewModel.killMessage?.let { message -> ActionFeedback(message, onRetry = { viewModel.loadProcesses() }, onDismiss = { viewModel.dismissKillMessage() }) }

        if (!viewModel.processesAvailable) {
            EmptyState(
                text = stringResource(R.string.error_capability_missing),
                icon = DesktopIcons.notice,
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
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = {
                    if (!viewModel.processesLoading) { keyboard?.hide(); viewModel.searchProcesses() }
                }),
                trailingIcon = {
                    if (viewModel.processFilter.isNotEmpty()) TextButton(onClick = {
                        viewModel.updateProcessFilter("")
                        viewModel.searchProcesses()
                    }, enabled = !viewModel.processesLoading) { Text(stringResource(R.string.taskmanager_clear_filter)) }
                },
                label = { Text(stringResource(R.string.manage_processes_filter)) },
                leadingIcon = { DesktopIcon(icon = DesktopIcons.search, size = 20.dp) },
                shape = MaterialTheme.shapes.medium,
            )
            Button(onClick = { keyboard?.hide(); viewModel.searchProcesses() }, enabled = !viewModel.processesLoading) {
                Text(stringResource(R.string.common_search))
            }
        }

        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            androidx.compose.foundation.layout.Box {
                TextButton(onClick = { sorting = true }, enabled = !viewModel.processesLoading) {
                    Text(stringResource(processSortLabel(viewModel.processSort)))
                }
                DropdownMenu(expanded = sorting, onDismissRequest = { sorting = false }) {
                    ProcessSort.entries.forEach { sort -> DropdownMenuItem(text = { Text(stringResource(processSortLabel(sort))) },
                        onClick = { sorting = false; viewModel.changeProcessSort(sort) }) }
                }
            }
            TextButton(onClick = { viewModel.toggleProcessDirection() }, enabled = !viewModel.processesLoading) {
                Text(stringResource(if (viewModel.processDescending) R.string.manage_processes_descending else R.string.manage_processes_ascending))
            }
            androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
            FilledTonalIconButton(onClick = { viewModel.loadProcesses() }, enabled = !viewModel.processesLoading) {
                DesktopIcon(DesktopIcons.refresh, size = 22.dp, contentDescription = stringResource(R.string.common_refresh))
            }
        }

        viewModel.processSampledAt?.let { time -> Text(stringResource(R.string.manage_processes_sampled_at,
            formatTimestamp(IsoInstant.toEpochMillis(time)).orEmpty()), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant) }
        RefreshProgressIndicator(visible = viewModel.processesLoading)
        if (viewModel.processItems.isEmpty()) {
            EmptyState(
                text = stringResource(
                    if (viewModel.processesLoading) R.string.common_loading else R.string.manage_processes_empty,
                ),
                icon = DesktopIcons.processes,
            )
        } else {
            BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
                val split = monitorUsesTwoPanes(maxWidth.value, maxHeight.value, LocalDensity.current.fontScale)
                BackHandler(enabled = active && !split && viewModel.processSelected != null) { viewModel.selectProcess(null) }
                if (split) {
                    Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                        ProcessList(viewModel, Modifier.width((360 * LocalDensity.current.fontScale).coerceAtMost(460f).dp))
                        val selected = viewModel.processSelected
                        if (selected != null) ProcessDetails(selected, viewModel, Modifier.weight(1f))
                        else Text(stringResource(R.string.manage_processes_select_details), modifier = Modifier.weight(1f))
                    }
                } else {
                    val selected = viewModel.processSelected
                    if (selected != null) ProcessDetails(selected, viewModel, Modifier.fillMaxSize())
                    else ProcessList(viewModel, Modifier.fillMaxSize())
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
            message = stringResource(R.string.manage_processes_kill_instance_message, process.name, process.pid, process.startTime.orEmpty()),
            confirmLabel = stringResource(R.string.manage_processes_kill),
            busy = viewModel.processesLoading,
            onConfirm = { viewModel.confirmKill() },
            onDismiss = {
                viewModel.cancelKill()
            },
        )
    }
}

private fun processSortLabel(sort: ProcessSort): Int = when (sort) {
    ProcessSort.Cpu -> R.string.manage_processes_sort_cpu
    ProcessSort.Memory -> R.string.manage_processes_sort_memory
    ProcessSort.Name -> R.string.manage_processes_sort_name
    ProcessSort.Pid -> R.string.manage_processes_sort_pid
}

@Composable
private fun ProcessList(model: ManageViewModel, modifier: Modifier) {
    SectionGroup(modifier) {
        LazyColumn(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            items(model.processItems, key = { "${it.pid}:${it.startTime}" }) { process ->
                ListRow(title = process.name, subtitle = stringResource(R.string.manage_processes_pid, process.pid),
                    supporting = listOfNotNull(stringResource(R.string.manage_processes_cpu, process.cpuPercent), formatSize(process.memoryBytes), process.userName).joinToString(" · "),
                    leading = { IconBadge(icon = DesktopIcons.processes) },
                    onClick = { model.selectProcess(process) }, selected = model.processSelected?.let { it.pid == process.pid && it.startTime == process.startTime } == true)
            }
        }
    }
}

@Composable
private fun ProcessDetails(process: RemoteProcess, model: ManageViewModel, modifier: Modifier) {
    val rows = listOf(
        R.string.manage_processes_sort_name to process.name,
        R.string.manage_processes_sort_pid to process.pid.toString(),
        R.string.manage_processes_sort_cpu to stringResource(R.string.home_value_percent, process.cpuPercent),
        R.string.manage_processes_sort_memory to formatSize(process.memoryBytes).orEmpty(),
        R.string.home_label_user to (process.userName ?: stringResource(R.string.monitor_unknown)),
        R.string.monitor_thread_count to stringResource(R.string.monitor_count, process.threadCount.toLong()),
        R.string.manage_processes_started_at to (process.startTime ?: stringResource(R.string.monitor_unknown)),
    )
    LazyColumn(modifier, verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        item {
            TextButton(onClick = { model.selectProcess(null) }) { Text(stringResource(R.string.taskmanager_back_to_list)) }
            Text(process.name, style = MaterialTheme.typography.titleLarge)
        }
        items(rows) { (label, value) ->
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(stringResource(label), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                SelectionContainer { Text(value, style = MaterialTheme.typography.bodyLarge) }
            }
        }
        item {
            Button(onClick = { model.requestKill(process) }, enabled = process.startTime != null && !model.processesLoading,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)) { Text(stringResource(R.string.manage_processes_kill)) }
            if (process.startTime == null) Text(stringResource(R.string.manage_processes_instance_unavailable))
        }
    }
}
