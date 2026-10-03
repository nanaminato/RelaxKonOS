package app.relaxkonos.mobile.ui.files

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.FileBatchAction
import app.relaxkonos.mobile.data.FileBrowserPolicy
import app.relaxkonos.mobile.data.FileSort
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.common.RemotePathPicker
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable
fun FileBrowserControls(vm: FilesViewModel) {
    var options by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        OutlinedTextField(vm.query, { vm.query = it }, label = { Text(stringResource(R.string.files_search)) },
            singleLine = true, modifier = Modifier.weight(1f))
        Box {
            TextButton(onClick = { options = true }) { Text(stringResource(R.string.files_view_options)) }
            DropdownMenu(options, onDismissRequest = { options = false }) {
                DropdownMenuItem(text = { Text(stringResource(if (vm.showHidden) R.string.files_hide_hidden else R.string.files_show_hidden)) },
                    onClick = { vm.showHidden = !vm.showHidden; options = false })
                FileSort.entries.forEach { sort ->
                    DropdownMenuItem(text = { Text(stringResource(when (sort) {
                        FileSort.Name -> R.string.files_sort_name
                        FileSort.Modified -> R.string.files_sort_modified
                        FileSort.Size -> R.string.files_sort_size
                        FileSort.Type -> R.string.files_sort_type
                    }) + if (vm.sort == sort) " ✓" else "") }, onClick = { vm.sort = sort; options = false })
                }
                DropdownMenuItem(text = { Text(stringResource(if (vm.sortDescending) R.string.files_sort_ascending else R.string.files_sort_descending)) },
                    onClick = { vm.sortDescending = !vm.sortDescending; options = false })
                DropdownMenuItem(text = { Text(stringResource(R.string.files_select)) },
                    onClick = { vm.toggleSelection(); options = false }, enabled = !vm.batchRunning)
            }
        }
    }
    if (vm.selectionMode) {
        Text(stringResource(R.string.files_selection_count, vm.checkedPaths.size, FileBrowserPolicy.MAX_BATCH))
        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState())) {
            TextButton(onClick = vm::selectVisible, enabled = !vm.batchRunning) { Text(stringResource(R.string.files_select_visible)) }
            TextButton(onClick = vm::toggleSelection, enabled = !vm.batchRunning) { Text(stringResource(R.string.common_cancel)) }
            TextButton(onClick = { vm.copySelection(false) }, enabled = vm.canMutate && vm.checkedPaths.isNotEmpty()) { Text(stringResource(R.string.files_action_copy)) }
            TextButton(onClick = { vm.copySelection(true) }, enabled = vm.canMutate && vm.checkedPaths.isNotEmpty()) { Text(stringResource(R.string.files_cut)) }
            TextButton(onClick = vm::requestBatchDelete, enabled = vm.canMutate && vm.checkedPaths.isNotEmpty()) { ActionLabel(R.string.common_delete) }
        }
    }
    vm.clipboard?.let { clipboard ->
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.files_clipboard_count, clipboard.entries.size), modifier = Modifier.weight(1f))
            TextButton(onClick = vm::paste, enabled = vm.canMutate && vm.path.isNotBlank()) { Text(stringResource(R.string.files_paste)) }
            TextButton(onClick = vm::clearClipboard, enabled = !vm.batchRunning) { Text(stringResource(R.string.files_clipboard_clear)) }
        }
    }
}

@Composable
fun FileBatchOverlays(vm: FilesViewModel) {
    vm.batchTarget?.let { target ->
        var directory by remember(target) { mutableStateOf(vm.path) }
        var browse by remember { mutableStateOf(false) }
        AlertDialog(onDismissRequest = vm::cancelBatchTarget,
            title = { Text(stringResource(when (target.action) {
                FileBatchAction.Copy -> R.string.files_copy_title
                FileBatchAction.Move -> R.string.files_move_title
                FileBatchAction.Delete -> R.string.files_delete_title
            })) }, text = {
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(stringResource(R.string.files_batch_confirmation, target.entries.size))
                    target.entries.forEach { Text(it.path) }
                    if (target.action != FileBatchAction.Delete) {
                        OutlinedTextField(directory, { directory = it }, label = { Text(stringResource(R.string.files_destination_directory)) }, singleLine = true)
                        OutlinedButton(onClick = { browse = true }) { Text(stringResource(R.string.remote_path_browse)) }
                        Text(stringResource(R.string.files_no_overwrite))
                    }
                }
            }, confirmButton = {
                Button(onClick = { vm.confirmBatch(directory) }, enabled = vm.canMutate &&
                    (target.action == FileBatchAction.Delete || FileBrowserPolicy.validDestination(target.entries, directory))) {
                    Text(stringResource(R.string.files_batch_confirm))
                }
            }, dismissButton = { TextButton(onClick = vm::cancelBatchTarget) { Text(stringResource(R.string.common_cancel)) } })
        if (browse) RemotePathPicker(kind = RemotePathKind.Directory, initialPath = directory,
            title = R.string.files_destination_directory, onDismiss = { browse = false },
            onSelect = { directory = it; browse = false })
    }
    vm.batchReport?.let { report ->
        AlertDialog(onDismissRequest = {}, title = { Text(stringResource(R.string.files_batch_result)) }, text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.files_batch_summary, report.completed.size, report.failures.size, report.skipped.size))
                report.completed.forEach { Text(stringResource(R.string.files_batch_completed, it)) }
                report.failures.forEach { failure ->
                    Text(stringResource(if (failure.unknown) R.string.files_batch_unknown else R.string.files_batch_failed, failure.path))
                    if (!failure.unknown) failure.result.failureMessage()?.let { Text(it.text()) }
                }
                Text(stringResource(R.string.files_batch_partial_hint))
                report.skipped.forEach { Text(stringResource(R.string.files_batch_skipped, it)) }
                if (report.failures.any { it.unknown }) Text(stringResource(R.string.files_mutation_unknown))
            }
        }, confirmButton = { TextButton(onClick = vm::dismissBatchReport) { Text(stringResource(R.string.common_close)) } },
            dismissButton = { TextButton(onClick = vm::refresh) { ActionLabel(R.string.common_refresh) } })
    }
}

@Composable
fun FilePermissionDialog(vm: FilesViewModel) {
    if (!vm.permissionsOpen) return
    val mode = FileBrowserPolicy.parseMode(vm.permissionInput)
    AlertDialog(onDismissRequest = vm::closePermissions, title = { Text(stringResource(R.string.files_permissions_edit)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(vm.selected?.path.orEmpty())
                Text(stringResource(R.string.files_permissions_scope))
                if (vm.selected?.isDirectory == true) Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(checked = vm.permissionRecursive, onCheckedChange = { vm.permissionRecursive = it }, enabled = !vm.mutationBusy)
                    Text(stringResource(R.string.files_permissions_recursive))
                }
                OutlinedTextField(vm.permissionInput, { vm.permissionInput = it }, singleLine = true, enabled = !vm.mutationBusy,
                    label = { Text(stringResource(R.string.files_permissions_octal)) }, isError = mode == null)
                val labels = listOf(R.string.files_permissions_owner, R.string.files_permissions_group, R.string.files_permissions_others)
                labels.forEachIndexed { index, label ->
                    Text(stringResource(label))
                    Row(Modifier.horizontalScroll(rememberScrollState()), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                        listOf(R.string.files_permissions_read, R.string.files_permissions_write, R.string.files_permissions_execute).forEachIndexed { bit, name ->
                            val mask = 1 shl (8 - index * 3 - bit)
                            Checkbox(checked = mode != null && mode and mask != 0, enabled = mode != null && !vm.mutationBusy,
                                onCheckedChange = { checked -> if (mode != null) vm.permissionInput = FileBrowserPolicy.formatMode(
                                    if (checked) mode or mask else mode and mask.inv()) })
                            Text(stringResource(name))
                        }
                    }
                }
                Text(stringResource(R.string.files_permissions_special))
            }
        }, confirmButton = { Button(onClick = vm::savePermissions, enabled = mode != null && vm.canMutate) {
            ActionLabel(R.string.common_save)
        } }, dismissButton = { TextButton(onClick = vm::closePermissions, enabled = !vm.mutationBusy) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable
fun FileLocationDialog(vm: FilesViewModel, onClose: () -> Unit) {
    var path by remember { mutableStateOf(vm.path) }
    var browse by remember { mutableStateOf(false) }
    AlertDialog(onDismissRequest = onClose, title = { Text(stringResource(R.string.files_go_directory)) },
        text = { Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedTextField(path, { path = it }, singleLine = true, label = { Text(stringResource(R.string.files_label_path)) })
            Text(stringResource(R.string.files_location_scope))
            OutlinedButton(onClick = { browse = true }) { Text(stringResource(R.string.remote_path_browse)) }
        } }, confirmButton = { Button(onClick = { vm.open(path); onClose() }, enabled = !vm.batchRunning && !vm.mutationBusy) {
            Text(stringResource(R.string.files_go_directory))
        } }, dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.common_cancel)) } })
    if (browse) RemotePathPicker(kind = RemotePathKind.Directory, initialPath = path,
        title = R.string.files_go_directory, onDismiss = { browse = false }, onSelect = { path = it; browse = false })
}
