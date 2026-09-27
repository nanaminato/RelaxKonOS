package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.stringResource
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
import app.relaxkonos.mobile.ui.common.SectionGroup
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

/**
 * SFTP-only browser for a host whose key was already trusted in Server Centre.
 *
 * The password is inherited from the just-verified workspace and never rendered here. Each operation opens a fresh, pinned SSH session,
 * so backgrounding or leaving this page cannot leave an unauthenticated transport around and a
 * changed host key still blocks every read and write.
 */
class SshFilesViewModel(application: Application) : AndroidViewModel(application) {
    private val app = getApplication<RelaxKonApplication>()
    private val container = app.container
    private val stateFlow = MutableStateFlow(SshFilesUiState())
    val state = stateFlow.asStateFlow()

    private var startedHostId: String? = null

    fun start() {
        if (state.value.hostId.isBlank() || startedHostId == state.value.hostId) return
        startedHostId = state.value.hostId
        reload()
    }

    fun reload() = execute { transport ->
        val currentPath = state.value.path
        val entries = transport.listDirectory(currentPath)
        update { copy(entries = entries, connected = true, busy = false, problem = null) }
    }

    fun open(entry: SshFileEntry) {
        if (entry.isDirectory && !entry.isSymbolicLink) {
            update { copy(path = entry.path, selected = null, preview = SshPreview.None) }
            reload()
        } else {
            update { copy(selected = entry, detailEntry = entry, preview = SshPreview.None) }
            if (entry.isText) loadText(entry) else if (entry.isImage) loadImage(entry)
        }
    }

    fun up() {
        val current = state.value.path
        if (current == "/") return
        val parent = current.trimEnd('/').substringBeforeLast('/', missingDelimiterValue = "")
            .ifBlank { "/" }
        update { copy(path = parent, selected = null, preview = SshPreview.None) }
        reload()
    }

    fun closeDetail() = update { copy(detailEntry = null, selected = null, preview = SshPreview.None) }

    fun beginCreateDirectory() = update { copy(newDirectory = "") }
    fun cancelCreateDirectory() = update { copy(newDirectory = null) }
    fun setNewDirectory(value: String) = update { copy(newDirectory = value) }

    fun createDirectory() {
        val name = state.value.newDirectory?.trim().orEmpty()
        if (!safeName(name)) {
            update { copy(problem = "invalid-name") }
            return
        }
        val destination = child(state.value.path, name)
        update { copy(newDirectory = null) }
        execute { transport ->
            transport.createDirectory(destination)
            reloadFromOperation(transport)
        }
    }

    fun askDelete(entry: SshFileEntry) = update { copy(deleteTarget = entry) }
    fun dismissDelete() = update { copy(deleteTarget = null) }

    fun delete() {
        val target = state.value.deleteTarget ?: return
        update { copy(deleteTarget = null) }
        execute { transport ->
            transport.delete(target.path, recursive = target.isDirectory && !target.isSymbolicLink)
            reloadFromOperation(transport)
        }
    }

    fun beginRename(entry: SshFileEntry) = update { copy(renameTarget = entry, renameName = entry.name) }
    fun cancelRename() = update { copy(renameTarget = null, renameName = "") }
    fun setRename(value: String) = update { copy(renameName = value) }

    fun rename() {
        val target = state.value.renameTarget ?: return
        val name = state.value.renameName.trim()
        if (!safeName(name)) {
            update { copy(problem = "invalid-name") }
            return
        }
        update { copy(renameTarget = null, renameName = "") }
        execute { transport ->
            transport.rename(target.path, child(parentOf(target.path), name))
            reloadFromOperation(transport)
        }
    }

    /** Streams a document selected through SAF straight to the current SFTP directory. */
    fun upload(uri: Uri) {
        val name = displayName(uri)
        if (!safeName(name)) {
            update { copy(problem = "invalid-name") }
            return
        }
        val destination = child(state.value.path, name)
        execute { transport ->
            val source = app.contentResolver.openInputStream(uri) ?: throw IllegalStateException("connection-failed")
            source.use { transport.upload(it, contentLength(uri), destination, null) }
            reloadFromOperation(transport)
        }
    }

    fun requestDownload(entry: SshFileEntry) = update { copy(downloadTarget = entry, downloadLaunchPending = true) }
    fun downloadLaunchHandled() = update { copy(downloadLaunchPending = false) }

    /** Writes the SFTP stream into the user-selected SAF document; no broad storage permission is used. */
    fun downloadTo(destination: Uri?) {
        val target = state.value.downloadTarget ?: return
        update { copy(downloadTarget = null, downloadLaunchPending = false) }
        if (destination == null) return
        execute { transport ->
            val output = app.contentResolver.openOutputStream(destination) ?: throw IllegalStateException("connection-failed")
            output.use { transport.download(target.path, it) }
            update { copy(busy = false, problem = null) }
        }
    }

    fun updateText(value: String) = update {
        val open = preview as? SshPreview.Text ?: return
        copy(preview = open.copy(content = value, changed = true))
    }

    fun saveText() {
        val open = state.value.preview as? SshPreview.Text ?: return
        execute { transport ->
            val bytes = open.content.toByteArray(Charsets.UTF_8)
            transport.upload(ByteArrayInputStream(bytes), bytes.size.toLong(), open.entry.path, null)
            update { copy(preview = open.copy(changed = false), busy = false, problem = null) }
        }
    }

    private fun loadText(entry: SshFileEntry) {
        if ((entry.size ?: 0L) > MAX_TEXT_BYTES) {
            update { copy(problem = "text-too-large") }
            return
        }
        execute { transport ->
            val bytes = ByteArrayOutputStream().use { buffer ->
                transport.download(entry.path, buffer)
                buffer.toByteArray()
            }
            if (bytes.size > MAX_TEXT_BYTES) throw IllegalStateException("text-too-large")
            update {
                copy(preview = SshPreview.Text(entry, bytes.toString(Charsets.UTF_8)), busy = false, problem = null)
            }
        }
    }

    private fun loadImage(entry: SshFileEntry) = execute { transport ->
        val bytes = ByteArrayOutputStream().use { buffer ->
            transport.download(entry.path, buffer)
            buffer.toByteArray()
        }
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            ?: throw IllegalStateException("image-unreadable")
        update { copy(preview = SshPreview.Image(entry, bitmap), busy = false, problem = null) }
    }

    private suspend fun reloadFromOperation(transport: app.relaxkonos.mobile.servercenter.ServerCenterSshTransport) {
        val entries = transport.listDirectory(state.value.path)
        update { copy(entries = entries, selected = null, preview = SshPreview.None, busy = false, problem = null) }
    }

    private fun execute(action: suspend (app.relaxkonos.mobile.servercenter.ServerCenterSshTransport) -> Unit) {
        val secret = container.serverCenter.workspacePasswordCopy()
        if (secret == null) {
            update { copy(problem = "connection-failed") }
            return
        }
        update { copy(busy = true, problem = null) }
        viewModelScope.launch {
            try {
                container.serverCenterConnections.connect(
                    state.value.hostId,
                    SshCredential(SshCredentialKind.Password, secret, null),
                    System.currentTimeMillis(),
                ).use { session -> action(session.sshTransport) }
            } catch (error: Exception) {
                update { copy(busy = false, problem = error.message?.takeIf { it in SAFE_PROBLEMS } ?: "connection-failed") }
            } finally {
                secret.fill('\u0000')
            }
        }
    }

    fun setHost(hostId: String) {
        if (state.value.hostId != hostId) {
            startedHostId = null
            stateFlow.value = SshFilesUiState(hostId = hostId)
        }
    }

    private inline fun update(block: SshFilesUiState.() -> SshFilesUiState) = stateFlow.update(block)

    private fun displayName(uri: Uri): String = app.contentResolver.query(
        uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null,
    )?.use { cursor ->
        val column = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (column >= 0 && cursor.moveToFirst()) cursor.getString(column) else null
    } ?: ""

    private fun contentLength(uri: Uri): Long? = app.contentResolver.openAssetFileDescriptor(uri, "r")
        ?.use { descriptor -> descriptor.length.takeIf { it >= 0 } }

    private companion object {
        const val MAX_TEXT_BYTES = 1_048_576L
        val SAFE_PROBLEMS = setOf("text-too-large", "image-unreadable")
        fun child(parent: String, name: String) = if (parent == "/") "/$name" else parent.trimEnd('/') + "/" + name
        fun parentOf(path: String) = path.trimEnd('/').substringBeforeLast('/', "").ifBlank { "/" }
        fun safeName(value: String) = value.isNotBlank() && value != "." && value != ".." && '/' !in value && '\\' !in value
    }
}

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
    val renameTarget: SshFileEntry? = null,
    val renameName: String = "",
    val downloadTarget: SshFileEntry? = null,
    val downloadLaunchPending: Boolean = false,
)

sealed interface SshPreview {
    data object None : SshPreview
    data class Text(val entry: SshFileEntry, val content: String, val changed: Boolean = false) : SshPreview
    data class Image(val entry: SshFileEntry, val bitmap: Bitmap) : SshPreview
}

private val SshFileEntry.isText: Boolean get() = name.substringAfterLast('.', "").lowercase() in
    setOf("txt", "md", "json", "xml", "yaml", "yml", "toml", "ini", "conf", "log", "sh", "kt", "java", "cs", "js", "ts", "py", "html", "css")
private val SshFileEntry.isImage: Boolean get() = name.substringAfterLast('.', "").lowercase() in
    setOf("png", "jpg", "jpeg", "webp", "gif", "bmp")

@Composable
fun SshFilesScreen(hostId: String) {
    val model: SshFilesViewModel = viewModel()
    LaunchedEffect(hostId) { model.setHost(hostId); model.start() }
    val state by model.state.collectAsState()
    val pickUpload = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> uri?.let(model::upload) }
    val saveDownload = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream"), model::downloadTo)
    LaunchedEffect(state.downloadLaunchPending) {
        val target = state.downloadTarget
        if (state.downloadLaunchPending && target != null) {
            model.downloadLaunchHandled()
            saveDownload.launch(target.name)
        }
    }
    Column(Modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        state.problem?.let { Text(problemText(it), color = androidx.compose.material3.MaterialTheme.colorScheme.error) }
        if (!state.connected) {
            if (state.busy) CircularProgressIndicator()
            else OutlinedButton(model::reload, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_retry)) }
        } else {
            val detail = state.detailEntry
            if (detail != null) {
                SshFileDetail(
                    entry = detail,
                    preview = state.preview,
                    busy = state.busy,
                    onBack = model::closeDetail,
                    onDownload = { model.requestDownload(detail) },
                    onRename = { model.beginRename(detail) },
                    onDelete = { model.askDelete(detail) },
                    onTextChanged = model::updateText,
                    onSaveText = model::saveText,
                )
            } else {
            SectionCard(
                title = stringResource(R.string.ssh_files_title),
                subtitle = state.path,
                leading = DesktopIcons.folder,
                trailing = {
                    IconButton(onClick = model::reload, enabled = !state.busy) {
                        DesktopIcon(DesktopIcons.refresh, contentDescription = stringResource(R.string.ssh_files_refresh))
                    }
                },
            ) {
                Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), modifier = Modifier.fillMaxWidth()) {
                    OutlinedButton(model::up, enabled = state.path != "/" && !state.busy) { Text(stringResource(R.string.ssh_files_up)) }
                    OutlinedButton({ pickUpload.launch(arrayOf("*/*")) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_upload)) }
                    OutlinedButton(model::beginCreateDirectory, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_new_folder)) }
                }
            }
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                SectionGroup {
                    state.entries.forEach { entry ->
                        ListRow(
                            title = entry.name,
                            subtitle = if (entry.isDirectory) stringResource(R.string.ssh_files_folder) else formatSize(entry.size),
                            supporting = formatTimestamp(entry.modifiedAtEpochMillis),
                            leading = { IconBadge(DesktopIcons.fileFor(entry.name, entry.isDirectory)) },
                            trailing = { SshEntryMenu(entry, model::beginRename, model::askDelete, model::requestDownload, state.busy) },
                            onClick = { model.open(entry) },
                            selected = state.selected?.path == entry.path,
                        )
                    }
                }
                state.selected?.takeIf { !it.isDirectory }?.let { selected ->
                    SectionCard(title = selected.name, subtitle = formatSize(selected.size), leading = DesktopIcons.fileFor(selected.name, false)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
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
    state.newDirectory?.let { value -> NameDialog(stringResource(R.string.ssh_files_new_folder), value, model::setNewDirectory, model::createDirectory, model::cancelCreateDirectory) }
    state.renameTarget?.let { NameDialog(stringResource(R.string.ssh_files_rename), state.renameName, model::setRename, model::rename, model::cancelRename) }
    state.deleteTarget?.let { entry -> ConfirmDangerousDialog(stringResource(R.string.ssh_files_delete), stringResource(R.string.ssh_files_delete_note, entry.name), stringResource(R.string.ssh_files_delete), model::delete, model::dismissDelete, state.busy) }
}

@Composable
private fun SshEntryMenu(
    entry: SshFileEntry,
    onRename: (SshFileEntry) -> Unit,
    onDelete: (SshFileEntry) -> Unit,
    onDownload: (SshFileEntry) -> Unit,
    busy: Boolean,
) {
    var expanded by remember { mutableStateOf(false) }
    IconButton(onClick = { expanded = true }, enabled = !busy) {
        DesktopIcon(DesktopIcons.overflow, contentDescription = stringResource(R.string.ssh_files_actions))
    }
    DropdownMenu(expanded, { expanded = false }) {
        if (!entry.isDirectory) DropdownMenuItem({ Text(stringResource(R.string.ssh_files_download)) }, { onDownload(entry); expanded = false })
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_rename)) }, { onRename(entry); expanded = false })
        DropdownMenuItem({ Text(stringResource(R.string.ssh_files_delete)) }, { onDelete(entry); expanded = false })
    }
}

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
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md), modifier = Modifier.verticalScroll(rememberScrollState())) {
        ScreenHeader(title = entry.name, subtitle = entry.path, onBack = onBack)
        SectionCard(title = stringResource(R.string.ssh_files_properties), leading = DesktopIcons.fileFor(entry.name, false)) {
            KeyValueRow(stringResource(R.string.ssh_files_property_type), if (entry.isSymbolicLink) stringResource(R.string.ssh_files_link) else stringResource(R.string.ssh_files_file))
            KeyValueRow(stringResource(R.string.ssh_files_property_size), formatSize(entry.size).orEmpty())
            KeyValueRow(stringResource(R.string.ssh_files_property_modified), formatTimestamp(entry.modifiedAtEpochMillis).orEmpty())
        }
        SectionCard(title = stringResource(R.string.ssh_files_actions)) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
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
        OutlinedTextField(preview.content, onChange, Modifier.fillMaxWidth(), label = { Text(preview.entry.name) }, minLines = 8, enabled = !busy)
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
    else -> R.string.ssh_files_connection_failed
})
