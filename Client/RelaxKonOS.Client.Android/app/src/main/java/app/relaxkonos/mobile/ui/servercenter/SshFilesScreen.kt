package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Checkbox
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.key
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.servercenter.SshFileEntry
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class SshFilesUiState(
    val hostId: String = "",
    val path: String = "/",
    val entries: List<SshFileEntry> = emptyList(),
    val connected: Boolean = false,
    val busy: Boolean = false,
    val problem: String? = null,
    val selected: SshFileEntry? = null,
    val detailEntry: SshFileEntry? = null,
    val preview: SshPreview = SshPreview.None,
    val newDirectory: String? = null,
    val deleteTarget: SshFileEntry? = null,
    val deleteEntries: List<SshFileEntry> = emptyList(),
    val renameTarget: SshFileEntry? = null,
    val renameName: String = "",
    val downloadTarget: SshFileEntry? = null,
    val downloadLaunchPending: Boolean = false,
    val downloadEntries: List<SshFileEntry> = emptyList(),
    val downloadZip: Boolean = false,
    val selecting: Boolean = false,
    val selectedPaths: Set<String> = emptySet(),
    val clipboard: SshClipboard? = null,
    val unknown: Boolean = false,
    val checked: List<SshFileCheck> = emptyList(),
    val discardRequested: Boolean = false,
    val writesSettling: Boolean = false,
    val search: String = "",
    val sort: SshFileSort = SshFileSort.Name,
    val descending: Boolean = false,
    val history: List<String> = listOf("/"),
    val historyIndex: Int = 0,
) {
    val visibleEntries: List<SshFileEntry> get() {
        val comparator: Comparator<SshFileEntry> = when (sort) {
            SshFileSort.Name -> compareBy<SshFileEntry> { it.name.lowercase(java.util.Locale.ROOT) }
            SshFileSort.Modified -> compareBy { it.modifiedAtEpochMillis ?: Long.MIN_VALUE }
            SshFileSort.Type -> compareBy { if (it.isSymbolicLink) "link" else it.name.substringAfterLast('.', "").lowercase(java.util.Locale.ROOT) }
            SshFileSort.Size -> compareBy { it.size ?: -1 }
        }
        return entries.filter { it.name.contains(search, ignoreCase = true) }
            .sortedWith(compareByDescending<SshFileEntry> { it.isDirectory && !it.isSymbolicLink }.then(if (descending) comparator.reversed() else comparator))
    }
}
enum class SshFileSort { Name, Modified, Type, Size }

sealed interface SshPreview {
    data object None : SshPreview
    data class Text(val entry: SshFileEntry, val content: String, val changed: Boolean = false) : SshPreview
    data class Image(val entry: SshFileEntry, val bitmap: Bitmap) : SshPreview
}

internal val SshFileEntry.isText: Boolean get() = name.substringAfterLast('.', "").lowercase() in
    setOf("txt", "md", "json", "xml", "yaml", "yml", "toml", "ini", "conf", "log", "sh", "kt", "java", "cs", "js", "ts", "py", "html", "css")
internal val SshFileEntry.isImage: Boolean get() = name.substringAfterLast('.', "").lowercase() in
    setOf("png", "jpg", "jpeg", "webp", "gif", "bmp")

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SshFilesScreen(hostId: String, modifier: Modifier = Modifier) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    key(hostId, container.serverCenter.workspaceRevision) { SshFilesContent(hostId, modifier) }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SshFilesContent(hostId: String, modifier: Modifier) {
    val model: SshFilesViewModel = viewModel()
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val workspaceRevision = container.serverCenter.workspaceRevision
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, model) {
        val observer = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.stop() }
    }
    LaunchedEffect(hostId, workspaceRevision, resumed) { if (resumed) model.resume(hostId) else model.stop() }
    val state by model.state.collectAsState()
    BackHandler(enabled = state.detailEntry != null) { model.closeDetail() }
    var pendingUpload by remember(hostId, workspaceRevision) { mutableStateOf<List<Uri>>(emptyList()) }
    var pendingTree by remember(hostId, workspaceRevision) { mutableStateOf<Uri?>(null) }
    var pendingDownload by remember(hostId, workspaceRevision) { mutableStateOf<Pair<Boolean, Uri?>?>(null) }
    var pasteConfirm by remember(hostId, workspaceRevision) { mutableStateOf<Pair<SshClipboard, String>?>(null) }
    var adoptConfirm by remember(hostId, workspaceRevision) { mutableStateOf<List<SshFileCheck>?>(null) }
    var showTransferHelp by remember(hostId) { mutableStateOf(false) }
    var editAddress by remember(hostId) { mutableStateOf(false) }
    var showSort by remember(hostId) { mutableStateOf(false) }
    var fileActions by remember(hostId) { mutableStateOf(false) }
    val pickUpload = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris -> pendingUpload = uris }
    val pickTree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> pendingTree = uri }
    val saveDownload = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri -> pendingDownload = true to uri }
    LaunchedEffect(resumed, pendingUpload, pendingTree, pendingDownload, state.busy, state.hostId) {
        if (resumed && !model.state.value.busy && state.hostId == hostId && container.serverCenter.sshFilesHostId == hostId) {
            if (pendingUpload.isNotEmpty()) { model.upload(pendingUpload); pendingUpload = emptyList() }
            pendingTree?.let { model.upload(emptyList(), it); pendingTree = null }
            pendingDownload?.let { model.downloadTo(it.second); pendingDownload = null }
        }
    }
    LaunchedEffect(state.downloadLaunchPending) {
        val target = state.downloadTarget
        if (state.downloadLaunchPending && target != null) {
            model.downloadLaunchHandled()
            saveDownload.launch(if (state.downloadZip) target.name + ".zip" else target.name)
        }
    }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        if (state.busy) Text(stringResource(R.string.ssh_files_transfer_note), style = MaterialTheme.typography.bodySmall)
        if (state.busy) TextButton(model::cancelTransfer) { Text(stringResource(R.string.common_cancel)) }
        if (state.unknown) {
            Text(stringResource(R.string.ssh_files_unknown), color = MaterialTheme.colorScheme.error)
            TextButton(model::checkUnknown, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_check_unknown)) }
            Column(Modifier.heightIn(max = 160.dp).verticalScroll(rememberScrollState())) {
            state.checked.forEach { check -> Text(stringResource(R.string.ssh_files_check_result, check.path, stringResource(when (check.exists) {
                true -> R.string.ssh_files_exists; false -> R.string.ssh_files_missing; null -> R.string.ssh_files_check_failed
            })), style = MaterialTheme.typography.bodySmall) }
            }
            TextButton(onClick = { adoptConfirm = state.checked }, enabled = !state.busy && !state.writesSettling && state.checked.isNotEmpty() && state.checked.all { it.exists != null }) { Text(stringResource(R.string.ssh_files_adopt_facts)) }
        }
        OperationMessageDialog(state.problem?.takeUnless { state.busy }?.let { problemText(it) })
        if (!state.connected) {
            if (state.busy) CircularProgressIndicator()
            else OutlinedButton(model::reload, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_retry)) }
        } else {
            val detail = state.detailEntry
            if (detail != null) {
                SshFileDetail(
                    entry = detail,
                    preview = state.preview,
                    busy = state.busy || state.unknown,
                    onBack = model::closeDetail,
                    onDownload = { model.requestDownload(detail) },
                    onRename = { model.beginRename(detail) },
                    onDelete = { model.askDelete(detail) },
                    onTextChanged = model::updateText,
                    onSaveText = model::saveText,
                )
            } else {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.ssh_files_title), modifier = Modifier.weight(1f), style = MaterialTheme.typography.headlineSmall)
                    TextButton(onClick = { showTransferHelp = true }) { Text(stringResource(R.string.ssh_files_transfer_help)) }
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    IconButton(onClick = model::up, enabled = state.path != "/" && !state.busy) {
                        DesktopIcon(DesktopIcons.upload, contentDescription = stringResource(R.string.ssh_files_up))
                    }
                    TextButton(onClick = { editAddress = !editAddress }, modifier = Modifier.weight(1f), enabled = !state.busy) {
                        Text(state.path, modifier = Modifier.fillMaxWidth(), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    IconButton(onClick = model::backDirectory, enabled = !state.busy && state.historyIndex > 0) {
                        DesktopIcon(DesktopIcons.back, contentDescription = stringResource(R.string.ssh_files_back))
                    }
                    IconButton(onClick = model::reload, enabled = !state.busy) {
                        DesktopIcon(DesktopIcons.refresh, contentDescription = stringResource(R.string.ssh_files_refresh))
                    }
                }
                var address by remember(hostId, state.path) { mutableStateOf(state.path) }
                if (editAddress) {
                OutlinedTextField(value = address, onValueChange = { if (it.length <= 4096) address = it }, enabled = !state.busy, singleLine = true,
                    modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_files_address)) })
                FlowRow {
                    TextButton({ model.navigate(address); editAddress = false }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_go)) }
                    TextButton(model::forwardDirectory, enabled = !state.busy && state.historyIndex < state.history.lastIndex) { Text(stringResource(R.string.ssh_files_forward)) }
                    TextButton({ editAddress = false }) { Text(stringResource(R.string.common_cancel)) }
                }
                }
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(value = state.search, onValueChange = model::search, enabled = !state.busy, singleLine = true,
                    modifier = Modifier.weight(1f), label = { Text(stringResource(R.string.ssh_files_search)) })
                    TextButton(onClick = { showSort = !showSort }) { Text(stringResource(R.string.ssh_files_sort)) }
                }
                if (showSort) {
                FlowRow {
                    SshFileSort.entries.forEach { sort -> androidx.compose.material3.FilterChip(selected = state.sort == sort, onClick = { model.sort(sort) }, enabled = !state.busy,
                        label = { Text(stringResource(when (sort) { SshFileSort.Name -> R.string.ssh_files_sort_name; SshFileSort.Modified -> R.string.ssh_files_sort_modified;
                            SshFileSort.Type -> R.string.ssh_files_sort_type; SshFileSort.Size -> R.string.ssh_files_sort_size })) }) }
                    TextButton(model::reverseSort, enabled = !state.busy) { Text(stringResource(if (state.descending) R.string.ssh_files_sort_descending else R.string.ssh_files_sort_ascending)) }
                }
                }
                Text(stringResource(R.string.ssh_files_counts, state.visibleEntries.size, state.entries.size, state.selectedPaths.size), style = MaterialTheme.typography.bodySmall)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), modifier = Modifier.fillMaxWidth()) {
                    Button({ pickUpload.launch(arrayOf("*/*")) }, enabled = !state.busy && !state.unknown) { Text(stringResource(R.string.ssh_files_upload)) }
                    TextButton(model::selectMode, enabled = !state.busy) { Text(stringResource(if (state.selecting) R.string.ssh_files_selection_done else R.string.ssh_files_select_multiple)) }
                    androidx.compose.foundation.layout.Box {
                        TextButton(onClick = { fileActions = true }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_more)) }
                        DropdownMenu(expanded = fileActions, onDismissRequest = { fileActions = false }) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.ssh_files_new_folder)) }, enabled = !state.unknown,
                                onClick = { fileActions = false; model.beginCreateDirectory() })
                            DropdownMenuItem(text = { Text(stringResource(R.string.ssh_files_upload_folder)) }, enabled = !state.unknown,
                                onClick = { fileActions = false; pickTree.launch(null) })
                        }
                    }
                    if (state.selecting) TextButton(model::selectAllVisible, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_select_all)) }
                    if (state.selectedPaths.isNotEmpty()) {
                        TextButton({ model.clipboard(cut = false) }, enabled = !state.busy && !state.unknown) { Text(stringResource(R.string.ssh_files_copy)) }
                        TextButton({ model.clipboard(cut = true) }, enabled = !state.busy && !state.unknown) { Text(stringResource(R.string.ssh_files_cut)) }
                        TextButton({ model.askDelete() }, enabled = !state.busy && !state.unknown) { Text(stringResource(R.string.ssh_files_delete)) }
                        TextButton({ model.requestDownload() }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_download)) }
                    }
                }
                state.clipboard?.let { clipboard ->
                    Text(stringResource(R.string.ssh_files_clipboard, clipboard.entries.size, stringResource(if (clipboard.cut) R.string.ssh_files_cut else R.string.ssh_files_copy)))
                    FlowRow {
                        TextButton({ pasteConfirm = clipboard to state.path }, enabled = !state.busy && !state.unknown) { Text(stringResource(R.string.ssh_files_paste)) }
                        TextButton(model::clearClipboard, enabled = !state.busy) { Text(stringResource(R.string.common_cancel)) }
                    }
                }
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    state.visibleEntries.forEach { entry ->
                        ListRow(
                            title = entry.name,
                            subtitle = if (entry.isDirectory) stringResource(R.string.ssh_files_folder) else formatSize(entry.size),
                            supporting = formatTimestamp(entry.modifiedAtEpochMillis),
                            leading = { if (state.selecting) Checkbox(checked = entry.path in state.selectedPaths, onCheckedChange = { model.toggle(entry) }, enabled = !state.busy)
                                else IconBadge(DesktopIcons.fileFor(entry.name, entry.isDirectory)) },
                            trailing = { SshEntryMenu(entry, model::beginRename, { model.askDelete(it) }, { model.requestDownload(it) },
                                { model.clipboard(it, false) }, { model.clipboard(it, true) }, state.busy, state.unknown) },
                            onClick = { model.open(entry) },
                            selected = if (state.selecting) entry.path in state.selectedPaths else state.selected?.path == entry.path,
                        )
                    }
                    state.selected?.takeIf { !it.isDirectory }?.let { selected ->
                        SectionCard(title = selected.name, subtitle = formatSize(selected.size), leading = DesktopIcons.fileFor(selected.name, false)) {
                            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                OutlinedButton({ model.requestDownload(selected) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_download)) }
                                TextButton({ model.beginRename(selected) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_rename)) }
                                TextButton({ model.askDelete(selected) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_delete)) }
                            }
                            Preview(state.preview, model::updateText, model::saveText, state.busy)
                        }
                    }
                }
            }
        }
    }
    if (showTransferHelp) AlertDialog(onDismissRequest = { showTransferHelp = false },
        title = { Text(stringResource(R.string.ssh_files_transfer_help)) },
        text = { Text(stringResource(R.string.ssh_files_transfer_note)) },
        confirmButton = { TextButton(onClick = { showTransferHelp = false }) { Text(stringResource(R.string.common_close)) } })
    if (state.discardRequested) AlertDialog(onDismissRequest = model::cancelDiscard,
        title = { Text(stringResource(R.string.ssh_files_discard_title)) }, text = { Text(stringResource(R.string.ssh_files_discard_note)) },
        confirmButton = { TextButton(model::discardDetail) { Text(stringResource(R.string.ssh_files_discard)) } },
        dismissButton = { TextButton(model::cancelDiscard) { Text(stringResource(R.string.common_cancel)) } })
    pasteConfirm?.let { (clipboard, path) -> AlertDialog(onDismissRequest = { pasteConfirm = null }, title = { Text(stringResource(R.string.ssh_files_paste)) },
        text = { Text(stringResource(R.string.ssh_files_paste_confirm, clipboard.entries.size, path)) },
        confirmButton = { TextButton(enabled = !state.busy && !state.unknown && state.clipboard == clipboard && state.path == path, onClick = { model.paste(); pasteConfirm = null }) { Text(stringResource(R.string.ssh_files_paste)) } },
        dismissButton = { TextButton({ pasteConfirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    adoptConfirm?.let { checks -> AlertDialog(onDismissRequest = { adoptConfirm = null }, title = { Text(stringResource(R.string.ssh_files_adopt_facts)) },
        text = { Text(stringResource(R.string.ssh_files_adopt_note)) },
        confirmButton = { TextButton(enabled = !state.busy && !state.writesSettling && state.checked == checks && checks.isNotEmpty() && checks.all { it.exists != null }, onClick = { model.adoptFacts(); adoptConfirm = null }) { Text(stringResource(R.string.ssh_files_adopt_facts)) } },
        dismissButton = { TextButton({ adoptConfirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    state.newDirectory?.let { value -> NameDialog(stringResource(R.string.ssh_files_new_folder), value, model::setNewDirectory, model::createDirectory, model::cancelCreateDirectory) }
    state.renameTarget?.let { NameDialog(stringResource(R.string.ssh_files_rename), state.renameName, model::setRename, model::rename, model::cancelRename) }
    state.deleteTarget?.let { entry -> ConfirmDangerousDialog(stringResource(R.string.ssh_files_delete), if (state.deleteEntries.size == 1) stringResource(R.string.ssh_files_delete_note, entry.name) else stringResource(R.string.ssh_files_delete_many_note, state.deleteEntries.size), stringResource(R.string.ssh_files_delete), model::delete, model::dismissDelete, state.busy) }
}

@Composable
private fun SshEntryMenu(
    entry: SshFileEntry,
    onRename: (SshFileEntry) -> Unit,
    onDelete: (SshFileEntry) -> Unit,
    onDownload: (SshFileEntry) -> Unit,
    onCopy: (SshFileEntry) -> Unit,
    onCut: (SshFileEntry) -> Unit,
    busy: Boolean,
    unknown: Boolean,
) {
    var expanded by remember { mutableStateOf(false) }
    IconButton(onClick = { expanded = true }, enabled = !busy) {
        DesktopIcon(DesktopIcons.overflow, contentDescription = stringResource(R.string.ssh_files_actions))
    }
    DropdownMenu(expanded, { expanded = false }) {
        if (!entry.isSymbolicLink) DropdownMenuItem({ Text(stringResource(R.string.ssh_files_download)) }, { onDownload(entry); expanded = false })
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_copy)) }, { onCopy(entry); expanded = false }, enabled = !unknown)
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_cut)) }, { onCut(entry); expanded = false }, enabled = !unknown)
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_rename)) }, { onRename(entry); expanded = false }, enabled = !unknown)
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_delete)) }, { onDelete(entry); expanded = false }, enabled = !unknown)
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SshFileDetail(
    entry: SshFileEntry,
    preview: SshPreview,
    busy: Boolean,
    onBack: () -> Unit,
    onDownload: () -> Unit,
    onRename: () -> Unit,
    onDelete: () -> Unit,
    onTextChanged: (String) -> Unit,
    onSaveText: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md), modifier = Modifier.fillMaxWidth()) {
        ScreenHeader(
            title = entry.name,
            subtitle = entry.path,
            onBack = onBack,
            backAlignment = Alignment.Top,
        )
        SectionCard(title = stringResource(R.string.ssh_files_properties), leading = DesktopIcons.fileFor(entry.name, false)) {
            KeyValueRow(stringResource(R.string.ssh_files_property_type), if (entry.isSymbolicLink) stringResource(R.string.ssh_files_link) else stringResource(R.string.ssh_files_file))
            KeyValueRow(stringResource(R.string.ssh_files_property_size), formatSize(entry.size).orEmpty())
            KeyValueRow(stringResource(R.string.ssh_files_property_modified), formatTimestamp(entry.modifiedAtEpochMillis).orEmpty())
        }
        SectionCard(title = stringResource(R.string.ssh_files_actions)) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(onDownload, enabled = !busy) { Text(stringResource(R.string.ssh_files_download)) }
                TextButton(onRename, enabled = !busy) { Text(stringResource(R.string.ssh_files_rename)) }
                TextButton(onDelete, enabled = !busy) { Text(stringResource(R.string.ssh_files_delete)) }
            }
        }
        SectionCard(title = stringResource(R.string.ssh_files_preview)) {
            Preview(preview, onTextChanged, onSaveText, busy)
        }
    }
}

@Composable private fun Preview(preview: SshPreview, onChange: (String) -> Unit, onSave: () -> Unit, busy: Boolean) = when (preview) {
    SshPreview.None -> Unit
    is SshPreview.Image -> Image(preview.bitmap.asImageBitmap(), preview.entry.name, Modifier.fillMaxWidth())
    is SshPreview.Text -> Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        OutlinedTextField(preview.content, onChange, Modifier.fillMaxWidth(), label = { Text(preview.entry.name) }, minLines = 8, maxLines = 20, enabled = !busy)
        Button(onSave, enabled = preview.changed && !busy) { Text(stringResource(R.string.ssh_files_save)) }
    }
}

@Composable private fun NameDialog(title: String, value: String, onValue: (String) -> Unit, onConfirm: () -> Unit, onDismiss: () -> Unit) = AlertDialog(
    onDismissRequest = onDismiss, title = { Text(title) }, text = { OutlinedTextField(value, onValue, label = { Text(title) }, singleLine = true) },
    confirmButton = { TextButton(onConfirm) { Text(stringResource(R.string.common_save)) } }, dismissButton = { TextButton(onDismiss) { Text(stringResource(R.string.common_cancel)) } },
)

@Composable private fun problemText(problem: String): String = stringResource(when (problem) {
    "text-too-large" -> R.string.ssh_files_text_too_large
    "image-unreadable" -> R.string.ssh_files_image_unreadable
    "invalid-name" -> R.string.ssh_files_invalid_name
    "transfer-limit" -> R.string.ssh_files_transfer_limit
    "source-changed" -> R.string.ssh_files_source_changed
    "symlink-transfer" -> R.string.ssh_files_symlink_transfer
    "paste-self" -> R.string.ssh_files_paste_self
    "destination-exists" -> R.string.ssh_files_destination_exists
    else -> R.string.ssh_files_connection_failed
})
