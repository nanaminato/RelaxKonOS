package app.relaxkonos.mobile.ui.files

import android.app.Application
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
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
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ProgressSheet
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import java.io.File
import kotlinx.coroutines.launch

/** A long-running transfer the shell is showing progress for. */
data class Transfer(val label: String, val collapsed: Boolean = false)

/**
 * Files destination state.
 *
 * One holder serves both the list and the detail pane, so switching between them — including the
 * Expanded layout, where the detail is a pane rather than a pushed page — never re-fetches the
 * directory and never loses the selection.
 */
class FilesViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    var path by mutableStateOf("")
        private set

    var listing by mutableStateOf<DirectoryListing?>(null)
        private set

    var loading by mutableStateOf(false)
        private set

    var message by mutableStateOf<UiMessage?>(null)
        private set

    var selected by mutableStateOf<RemoteEntry?>(null)
        private set

    var properties by mutableStateOf<RemoteFileProperties?>(null)
        private set

    var propertiesLoading by mutableStateOf(false)
        private set

    var newDirectoryOpen by mutableStateOf(false)
        private set

    var renameTarget by mutableStateOf<RemoteEntry?>(null)
        private set

    var deleteTarget by mutableStateOf<RemoteEntry?>(null)
        private set

    var transfer by mutableStateOf<Transfer?>(null)
        private set

    private var started = false

    /** Loads the roots once per process; later loads are explicit refreshes. */
    fun start() {
        if (started) {
            return
        }
        started = true
        reload()
    }

    fun refresh() = reload()

    fun open(nextPath: String) {
        path = nextPath
        selected = null
        properties = null
        reload()
    }

    fun goUp() {
        if (path.isBlank()) {
            return
        }
        open(container.files.parentOf(path))
    }

    fun select(entry: RemoteEntry?) {
        selected = entry
        properties = null
        if (entry != null) {
            loadProperties(entry.path)
        }
    }

    fun dismissMessage() {
        message = null
    }

    fun openNewDirectory() {
        newDirectoryOpen = true
    }

    fun cancelNewDirectory() {
        newDirectoryOpen = false
    }

    fun confirmNewDirectory(name: String) {
        newDirectoryOpen = false
        val target = childOf(path, name)
        viewModelScope.launch {
            loading = true
            when (val result = container.files.createDirectory(target, container.elevationAnswers)) {
                is ApiResult.Success -> {
                    message = UiMessage(R.string.files_created, listOf(target))
                    reload()
                }

                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    fun requestRename(entry: RemoteEntry) {
        renameTarget = entry
    }

    fun cancelRename() {
        renameTarget = null
    }

    fun confirmRename(newName: String) {
        val target = renameTarget ?: return
        renameTarget = null
        viewModelScope.launch {
            loading = true
            when (val result = container.files.rename(target.path, newName, container.elevationAnswers)) {
                is ApiResult.Success -> reload()
                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    fun requestDelete(entry: RemoteEntry) {
        deleteTarget = entry
    }

    fun cancelDelete() {
        deleteTarget = null
    }

    fun confirmDelete() {
        val target = deleteTarget ?: return
        deleteTarget = null
        viewModelScope.launch {
            loading = true
            when (val result = container.files.delete(target.path, container.elevationAnswers)) {
                is ApiResult.Success -> {
                    if (selected?.path == target.path) {
                        select(null)
                    }
                    reload()
                }

                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    /**
     * Streams the file into the app cache directory.
     *
     * No progress fraction and no cancel affordance are offered, because the transport in
     * `core/net/RelaxKonApi.kt` neither reports byte counts nor accepts cancellation. Showing a
     * percentage or a cancel button that cannot cancel would be worse than showing none.
     */
    fun download(entry: RemoteEntry) {
        if (transfer != null) {
            return
        }
        val target = File(getApplication<RelaxKonApplication>().cacheDir, entry.name)
        transfer = Transfer(label = entry.path)
        viewModelScope.launch {
            when (val result = container.files.download(entry.path, target, container.elevationAnswers)) {
                is ApiResult.Success -> message = UiMessage(R.string.files_downloaded, listOf(target.absolutePath))
                else -> message = result.failureMessage()
            }
            transfer = null
        }
    }

    fun setTransferCollapsed(collapsed: Boolean) {
        transfer = transfer?.copy(collapsed = collapsed)
    }

    private fun loadProperties(targetPath: String) {
        propertiesLoading = true
        viewModelScope.launch {
            when (val result = container.files.properties(targetPath)) {
                is ApiResult.Success -> properties = result.value
                else -> message = result.failureMessage()
            }
            propertiesLoading = false
        }
    }

    private fun reload() {
        loading = true
        viewModelScope.launch {
            when (val result = container.files.list(path, container.elevationAnswers)) {
                is ApiResult.Success -> {
                    listing = result.value
                    path = result.value.path
                }

                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    private fun childOf(parent: String, name: String): String = when {
        parent.isBlank() || parent == "/" -> "/$name"
        else -> "${parent.trimEnd('/')}/$name"
    }
}

/**
 * The file list.
 *
 * The location bar is always visible, including while a directory is loading, because "where am I" is
 * a permanent part of this destination rather than something shown only on success.
 *
 * [onOpenDetail] is invoked when the user picks something the detail view must show. In the Expanded
 * layout the caller passes a no-op, because the detail is already on screen as a pane.
 */
@Composable
fun FilesScreen(
    onOpenDetail: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: FilesViewModel = viewModel()

    LaunchedEffect(Unit) { viewModel.start() }

    var menuForPath by remember { mutableStateOf<String?>(null) }

    Column(modifier = modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        viewModel.message?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.refresh() },
                onDismiss = { viewModel.dismissMessage() },
            )
        }

        Card(Modifier.fillMaxWidth()) {
            Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { viewModel.goUp() }, enabled = viewModel.path.isNotBlank()) {
                    Icon(Icons.Filled.KeyboardArrowUp, contentDescription = stringResource(R.string.files_action_up))
                }
                Text(
                    text = viewModel.path.ifBlank { stringResource(R.string.files_root) },
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                IconButton(onClick = { viewModel.refresh() }) {
                    Icon(Icons.Filled.Refresh, contentDescription = stringResource(R.string.common_refresh))
                }
                IconButton(onClick = { viewModel.openNewDirectory() }) {
                    Icon(Icons.Filled.Add, contentDescription = stringResource(R.string.files_action_new_directory))
                }
            }
        }

        val listing = viewModel.listing
        if (listing == null || listing.entries.isEmpty()) {
            EmptyHint(stringResource(if (viewModel.loading) R.string.common_loading else R.string.files_empty))
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                items(listing.entries, key = { it.path }) { entry ->
                    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .clickable {
                                    if (entry.isDirectory) {
                                        viewModel.open(entry.path)
                                    } else {
                                        viewModel.select(entry)
                                        onOpenDetail()
                                    }
                                }
                                .padding(vertical = 8.dp),
                        ) {
                            Text(
                                text = entry.name,
                                style = MaterialTheme.typography.bodyLarge,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = listOfNotNull(
                                    stringResource(
                                        if (entry.isDirectory) R.string.files_kind_directory else R.string.files_kind_file,
                                    ),
                                    formatSize(entry.sizeBytes),
                                    formatTimestamp(entry.modifiedAtMillis),
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Box {
                            IconButton(onClick = { menuForPath = entry.path }) {
                                Icon(Icons.Filled.MoreVert, contentDescription = stringResource(R.string.files_action_more))
                            }
                            DropdownMenu(
                                expanded = menuForPath == entry.path,
                                onDismissRequest = { menuForPath = null },
                            ) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.files_action_rename)) },
                                    leadingIcon = { Icon(Icons.Filled.Edit, contentDescription = null) },
                                    onClick = {
                                        menuForPath = null
                                        viewModel.requestRename(entry)
                                    },
                                )
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.common_delete)) },
                                    leadingIcon = { Icon(Icons.Filled.Delete, contentDescription = null) },
                                    onClick = {
                                        menuForPath = null
                                        viewModel.requestDelete(entry)
                                    },
                                )
                            }
                        }
                    }
                }
            }
        }

        viewModel.transfer?.let { transfer ->
            ProgressSheet(
                title = stringResource(R.string.files_downloading),
                detail = transfer.label,
                progress = null,
                collapsed = transfer.collapsed,
                onCollapsedChange = { viewModel.setTransferCollapsed(it) },
                onCancel = null,
            )
        }
    }

    if (viewModel.newDirectoryOpen) {
        NewDirectoryDialog(
            parentPath = viewModel.path,
            onDismiss = { viewModel.cancelNewDirectory() },
            onConfirm = { viewModel.confirmNewDirectory(it) },
        )
    }

    viewModel.renameTarget?.let { entry ->
        RenameDialog(
            entry = entry,
            onDismiss = { viewModel.cancelRename() },
            onConfirm = { viewModel.confirmRename(it) },
        )
    }

    viewModel.deleteTarget?.let { entry ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.files_delete_title),
            message = stringResource(R.string.files_delete_message, entry.path),
            confirmLabel = stringResource(R.string.common_delete),
            busy = viewModel.loading,
            onConfirm = { viewModel.confirmDelete() },
            onDismiss = { viewModel.cancelDelete() },
        )
    }
}

/**
 * Properties of the selected entry.
 *
 * [onBack] is `null` when the caller is rendering this as a pane; a pane has nothing to go back from,
 * and a visible-but-inert back button would misdescribe the layout.
 */
@Composable
fun FileDetailScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val viewModel: FilesViewModel = viewModel()
    val container = appContainer()
    val entry = viewModel.selected
    val properties = viewModel.properties

    Column(modifier = modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }
        if (entry == null) {
            EmptyHint(stringResource(R.string.files_detail_none))
            return@Column
        }

        SectionCard(stringResource(R.string.files_detail_title)) {
            KeyValueRow(stringResource(R.string.files_label_name), entry.name)
            KeyValueRow(stringResource(R.string.files_label_path), entry.path)
            KeyValueRow(
                stringResource(R.string.files_label_kind),
                stringResource(if (entry.isDirectory) R.string.files_kind_directory else R.string.files_kind_file),
            )
            formatSize(properties?.sizeBytes ?: entry.sizeBytes)?.let {
                KeyValueRow(stringResource(R.string.files_label_size), it)
            }
            formatTimestamp(properties?.modifiedMillis ?: entry.modifiedAtMillis)?.let {
                KeyValueRow(stringResource(R.string.files_label_modified), it)
            }
            formatTimestamp(properties?.createdMillis)?.let {
                KeyValueRow(stringResource(R.string.files_label_created), it)
            }
            if (container.capabilities.contains(ServerCapabilities.POSIX_PERMISSIONS)) {
                properties?.permissions?.takeIf { it.isNotBlank() }?.let {
                    KeyValueRow(stringResource(R.string.files_label_permissions), it)
                }
            }
            if (properties == null) {
                EmptyHint(
                    stringResource(
                        if (viewModel.propertiesLoading) R.string.common_loading else R.string.files_detail_unavailable,
                    ),
                )
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (!entry.isDirectory) {
                Button(onClick = { viewModel.download(entry) }, enabled = viewModel.transfer == null) {
                    Text(stringResource(R.string.files_action_download))
                }
            }
            TextButton(onClick = { viewModel.requestRename(entry) }) { Text(stringResource(R.string.files_action_rename)) }
            TextButton(onClick = { viewModel.requestDelete(entry) }) { Text(stringResource(R.string.common_delete)) }
        }
    }
}

@Composable
private fun NewDirectoryDialog(parentPath: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.files_new_directory_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.files_new_directory_body, parentPath.ifBlank { "/" }))
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.files_label_name)) },
                )
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(name.trim()) }, enabled = name.isNotBlank()) {
                Text(stringResource(R.string.common_create))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
private fun RenameDialog(entry: RemoteEntry, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf(entry.name) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.files_rename_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(entry.path)
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.files_label_name)) },
                )
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(name.trim()) }, enabled = name.isNotBlank() && name != entry.name) {
                Text(stringResource(R.string.common_save))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}
