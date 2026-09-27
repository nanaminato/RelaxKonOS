package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.weight
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
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
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.ScreenHeader
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
 * The password is deliberately only UI memory.  Each operation opens a fresh, pinned SSH session,
 * so backgrounding or leaving this page cannot leave an unauthenticated transport around and a
 * changed host key still blocks every read and write.
 */
class SshFilesViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    private val stateFlow = MutableStateFlow(SshFilesUiState())
    val state = stateFlow.asStateFlow()

    fun setPassword(value: String) = update { copy(password = value, problem = null) }

    fun connect() {
        if (state.value.password.isBlank()) return
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
            update { copy(selected = entry, preview = SshPreview.None) }
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
        val password = state.value.password
        if (password.isBlank()) return
        update { copy(busy = true, problem = null) }
        viewModelScope.launch {
            val secret = password.toCharArray()
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
        if (state.value.hostId != hostId) stateFlow.value = SshFilesUiState(hostId = hostId)
    }

    override fun onCleared() {
        stateFlow.value.password.toCharArray().fill('\u0000')
        super.onCleared()
    }

    private inline fun update(block: SshFilesUiState.() -> SshFilesUiState) = stateFlow.update(block)

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
    val password: String = "",
    val path: String = "/",
    val entries: List<SshFileEntry> = emptyList(),
    val connected: Boolean = false,
    val busy: Boolean = false,
    val problem: String? = null,
    val selected: SshFileEntry? = null,
    val preview: SshPreview = SshPreview.None,
    val newDirectory: String? = null,
    val deleteTarget: SshFileEntry? = null,
    val renameTarget: SshFileEntry? = null,
    val renameName: String = "",
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
fun SshFilesScreen(hostId: String, onClose: () -> Unit) {
    val model: SshFilesViewModel = viewModel()
    LaunchedEffect(hostId) { model.setHost(hostId) }
    val state by model.state.collectAsState()
    Column(Modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.ssh_files_title), onBack = onClose)
        state.problem?.let { Text(problemText(it), color = androidx.compose.material3.MaterialTheme.colorScheme.error) }
        if (!state.connected) {
            Text(stringResource(R.string.ssh_files_password_hint))
            PasswordTextField(
                value = state.password,
                onValueChange = model::setPassword,
                label = stringResource(R.string.server_center_ssh_password),
                enabled = !state.busy,
            )
            Button(model::connect, enabled = state.password.isNotBlank() && !state.busy, modifier = Modifier.fillMaxWidth()) {
                if (state.busy) CircularProgressIndicator() else Text(stringResource(R.string.ssh_files_connect))
            }
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), modifier = Modifier.fillMaxWidth()) {
                OutlinedButton(model::up, enabled = state.path != "/" && !state.busy) { Text(stringResource(R.string.ssh_files_up)) }
                OutlinedButton(model::reload, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_refresh)) }
                OutlinedButton(model::beginCreateDirectory, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_new_folder)) }
            }
            Text(state.path)
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                state.entries.forEach { entry ->
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        TextButton(onClick = { model.open(entry) }, modifier = Modifier.weight(1f), enabled = !state.busy) {
                            Text(entry.name)
                        }
                        TextButton(onClick = { model.beginRename(entry) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_rename)) }
                        TextButton(onClick = { model.askDelete(entry) }, enabled = !state.busy) { Text(stringResource(R.string.ssh_files_delete)) }
                    }
                }
                Preview(state.preview, model::updateText, model::saveText, state.busy)
            }
        }
    }
    state.newDirectory?.let { value -> NameDialog(stringResource(R.string.ssh_files_new_folder), value, model::setNewDirectory, model::createDirectory, model::cancelCreateDirectory) }
    state.renameTarget?.let { NameDialog(stringResource(R.string.ssh_files_rename), state.renameName, model::setRename, model::rename, model::cancelRename) }
    state.deleteTarget?.let { entry -> ConfirmDangerousDialog(stringResource(R.string.ssh_files_delete), stringResource(R.string.ssh_files_delete_note, entry.name), stringResource(R.string.ssh_files_delete), model::delete, model::dismissDelete, state.busy) }
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
