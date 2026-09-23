package app.relaxkonos.mobile.ui.files

import android.app.Application
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ProgressSheet
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import java.io.File
import androidx.core.content.FileProvider
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/** A long-running transfer the shell is showing progress for. */
data class Transfer(
    val label: String,
    val kind: TransferKind,
    val transferredBytes: Long = 0,
    val totalBytes: Long? = null,
    val collapsed: Boolean = false,
) {
    val progress: Float? get() = totalBytes?.takeIf { it > 0 }?.let { (transferredBytes.toFloat() / it).coerceIn(0f, 1f) }
}

enum class TransferKind { Download, Upload, Move, Copy }

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

    var transferTarget by mutableStateOf<TransferTarget?>(null)
        private set

    var downloadReady by mutableStateOf<File?>(null)
        private set

    var transfer by mutableStateOf<Transfer?>(null)
        private set

    private var transferJob: Job? = null

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
        val parent = container.files.navigationParentOf(path)
        if (path.isBlank() || parent == path) {
            return
        }
        open(parent)
    }

    val canGoUp: Boolean
        get() = path.isNotBlank() && container.files.navigationParentOf(path) != path

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
        val target = container.files.childOf(path, name)
        viewModelScope.launch {
            loading = true
            when (val result = container.files.createDirectory(target, container.elevationAnswers)) {
                is ApiResult.Success -> {
                    message = UiMessage(R.string.files_created, listOf(target))
                    container.recentOperations.record(RecentOperationKind.CreateDirectory, target)
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
                is ApiResult.Success -> {
                    container.recentOperations.record(RecentOperationKind.Rename, target.path)
                    reload()
                }
                else -> message = result.failureMessage()
            }
            loading = false
        }
    }

    fun requestDelete(entry: RemoteEntry) {
        deleteTarget = entry
    }

    fun requestTransfer(entry: RemoteEntry, move: Boolean) {
        transferTarget = TransferTarget(entry, move)
    }

    fun cancelTransferTarget() {
        transferTarget = null
    }

    fun confirmTransfer(destinationPath: String) {
        val request = transferTarget ?: return
        transferTarget = null
        if (destinationPath.isBlank() || transfer != null) {
            return
        }
        val label = request.entry.path
        transfer = Transfer(label = label, kind = if (request.move) TransferKind.Move else TransferKind.Copy)
        transferJob = viewModelScope.launch {
            try {
                val result = if (request.move) {
                    container.files.move(request.entry.path, destinationPath, container.elevationAnswers)
                } else {
                    container.files.copy(request.entry.path, destinationPath, container.elevationAnswers)
                }
                if (result is ApiResult.Success) {
                    container.recentOperations.record(
                        if (request.move) RecentOperationKind.Move else RecentOperationKind.Copy,
                        request.entry.path,
                    )
                    if (request.move && selected?.path == request.entry.path) select(null)
                    reload()
                } else {
                    message = result.failureMessage()
                }
            } catch (_: CancellationException) {
                // The transfer card disappears; cancellation is an expected user action.
            } finally {
                transfer = null
                transferJob = null
            }
        }
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
                    container.recentOperations.record(RecentOperationKind.Delete, target.path)
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

    /** Streams a remote file into private cache, then lets the screen hand it to Android's share sheet. */
    fun download(entry: RemoteEntry) {
        if (transfer != null) {
            return
        }
        val target = File(getApplication<RelaxKonApplication>().cacheDir, entry.name)
        transfer = Transfer(label = entry.path, kind = TransferKind.Download)
        transferJob = viewModelScope.launch {
            try {
                val result = container.files.download(entry.path, target, container.elevationAnswers) { written, total ->
                    viewModelScope.launch(Dispatchers.Main.immediate) {
                        transfer = transfer?.takeIf { it.kind == TransferKind.Download }?.copy(
                            transferredBytes = written,
                            totalBytes = total,
                        )
                    }
                }
                when (result) {
                    is ApiResult.Success -> {
                        container.recentOperations.record(RecentOperationKind.Download, entry.path)
                        downloadReady = target
                    }
                    else -> message = result.failureMessage()
                }
            } catch (_: CancellationException) {
                // Expected when the user cancels the transfer.
            } finally {
                transfer = null
                transferJob = null
            }
        }
    }

    /** Copies a user-selected document stream directly to the server; no broad storage permission is needed. */
    fun upload(uri: Uri) {
        if (transfer != null) return
        val resolver = getApplication<RelaxKonApplication>().contentResolver
        val name = resolver.displayName(uri) ?: getApplication<RelaxKonApplication>().getString(R.string.files_upload_default_name)
        val length = runCatching { resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } }
            .getOrNull()
            ?.takeIf { it >= 0 }
        transfer = Transfer(label = name, kind = TransferKind.Upload, totalBytes = length)
        transferJob = viewModelScope.launch {
            try {
                val result = runCatching {
                    resolver.openInputStream(uri)?.use { input ->
                        container.files.upload(path, name, input, length, container.elevationAnswers) { written ->
                            viewModelScope.launch(Dispatchers.Main.immediate) {
                                transfer = transfer?.takeIf { it.kind == TransferKind.Upload }?.copy(transferredBytes = written)
                            }
                        }
                    } ?: ApiResult.Transport(null)
                }.getOrElse {
                    if (it is CancellationException) throw it
                    ApiResult.Transport(it.message)
                }
                when (result) {
                    is ApiResult.Success -> {
                        message = UiMessage(R.string.files_uploaded, listOf(name))
                        container.recentOperations.record(RecentOperationKind.Upload, name)
                        reload()
                    }
                    else -> message = result.failureMessage()
                }
            } catch (_: CancellationException) {
                // Expected when the user cancels the transfer.
            } finally {
                transfer = null
                transferJob = null
            }
        }
    }

    fun cancelActiveTransfer() {
        transferJob?.cancel()
        transferJob = null
        transfer = null
    }

    fun consumeDownloadReady() {
        downloadReady = null
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

}

data class TransferTarget(val entry: RemoteEntry, val move: Boolean)

private fun android.content.ContentResolver.displayName(uri: Uri): String? = runCatching {
    query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor: Cursor ->
        cursor.takeIf { it.moveToFirst() }?.getString(cursor.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME))
    }
}.getOrNull()

/**
 * The file list.
 *
 * The location bar is always visible, including while a directory is loading, because "where am I" is
 * a permanent part of this destination rather than something shown only on success. It is a single row
 * of three controls — up, path, refresh — with the two write actions on their own line, so "navigate"
 * and "change something" never sit under the same thumb.
 *
 * [onOpenDetail] is invoked when the user picks something the detail view must show. In the Expanded
 * layout the caller passes a no-op, because the detail is already on screen as a pane.
 */
@Composable
fun FilesScreen(
    viewModel: FilesViewModel,
    onOpenDetail: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val pickUpload = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri?.let(viewModel::upload)
    }

    LaunchedEffect(Unit) { viewModel.start() }

    var menuForPath by remember { mutableStateOf<String?>(null) }

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        ScreenHeader(title = stringResource(R.string.nav_files))

        viewModel.message?.let { banner ->
            ErrorBanner(
                message = banner.text(),
                onRetry = { viewModel.refresh() },
                onDismiss = { viewModel.dismissMessage() },
            )
        }

        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            FilledTonalIconButton(onClick = { viewModel.goUp() }, enabled = viewModel.canGoUp) {
                DesktopIcon(
                    icon = DesktopIcons.parentDirectory,
                    size = 24.dp,
                    contentDescription = stringResource(R.string.files_action_up),
                )
            }
            LocationBar(
                path = viewModel.path.ifBlank { stringResource(R.string.files_root) },
                modifier = Modifier.weight(1f),
            )
            IconButton(onClick = { viewModel.refresh() }) {
                DesktopIcon(
                    icon = DesktopIcons.refresh,
                    size = 24.dp,
                    contentDescription = stringResource(R.string.common_refresh),
                )
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { viewModel.openNewDirectory() }, modifier = Modifier.weight(1f)) {
                DesktopIcon(icon = DesktopIcons.newFolder, size = 18.dp)
                Spacer(Modifier.width(Spacing.sm))
                Text(stringResource(R.string.files_action_new_directory))
            }
            Button(
                onClick = { pickUpload.launch(arrayOf("*/*")) },
                enabled = viewModel.transfer == null,
                modifier = Modifier.weight(1f),
            ) {
                DesktopIcon(icon = DesktopIcons.upload, size = 18.dp)
                Spacer(Modifier.width(Spacing.sm))
                Text(stringResource(R.string.files_action_upload))
            }
        }

        val listing = viewModel.listing
        if (listing == null || listing.entries.isEmpty()) {
            EmptyState(
                text = stringResource(if (viewModel.loading) R.string.common_loading else R.string.files_empty),
                icon = DesktopIcons.notice,
                modifier = Modifier.weight(1f),
            )
        } else {
            LazyColumn(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(Spacing.xs),
            ) {
                items(listing.entries, key = { it.path }) { entry ->
                    FileEntryRow(
                        entry = entry,
                        menuOpen = menuForPath == entry.path,
                        onOpenMenu = { menuForPath = entry.path },
                        onCloseMenu = { menuForPath = null },
                        onOpen = {
                            if (entry.isDirectory) {
                                viewModel.open(entry.path)
                            } else {
                                viewModel.select(entry)
                                onOpenDetail()
                            }
                        },
                        viewModel = viewModel,
                    )
                }
            }
        }

        viewModel.transfer?.let { transfer ->
            ProgressSheet(
                title = stringResource(
                    when (transfer.kind) {
                        TransferKind.Download -> R.string.files_downloading
                        TransferKind.Upload -> R.string.files_uploading
                        TransferKind.Move -> R.string.files_moving
                        TransferKind.Copy -> R.string.files_copying
                    },
                ),
                detail = transfer.label,
                progress = transfer.progress,
                collapsed = transfer.collapsed,
                onCollapsedChange = { viewModel.setTransferCollapsed(it) },
                onCancel = viewModel::cancelActiveTransfer,
            )
        }
    }

    LaunchedEffect(viewModel.downloadReady) {
        val file = viewModel.downloadReady ?: return@LaunchedEffect
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        context.startActivity(
            Intent.createChooser(
                Intent(Intent.ACTION_SEND)
                    .setType("application/octet-stream")
                    .putExtra(Intent.EXTRA_STREAM, uri)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
                context.getString(R.string.files_share_download),
            ),
        )
        viewModel.consumeDownloadReady()
    }
}

/**
 * The current directory.
 *
 * It is a recessed surface rather than a heading, because it changes as often as the user taps and a
 * heading that rewrites itself reads as a title for the wrong thing. The path is truncated from the
 * end: the tail of a path is the part that says where you are.
 */
@Composable
private fun LocationBar(path: String, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier,
        shape = MaterialTheme.shapes.medium,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
    ) {
        Text(
            text = path,
            style = MaterialTheme.typography.titleSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = Spacing.md, vertical = Spacing.sm + 2.dp),
        )
    }
}

/**
 * One directory entry.
 *
 * The glyph is chosen from the name, exactly as the desktop Explorer chooses it, so a Kotlin file, a
 * PDF and an archive are told apart without reading the name. Only the per-row menu carries the
 * operations — none of them is destructive on tap.
 */
@Composable
private fun FileEntryRow(
    entry: RemoteEntry,
    menuOpen: Boolean,
    onOpenMenu: () -> Unit,
    onCloseMenu: () -> Unit,
    onOpen: () -> Unit,
    viewModel: FilesViewModel,
) {
    ListRow(
        title = entry.name,
        supporting = listOfNotNull(
            stringResource(if (entry.isDirectory) R.string.files_kind_directory else R.string.files_kind_file),
            formatSize(entry.sizeBytes),
            formatTimestamp(entry.modifiedAtMillis),
        ).joinToString(" · "),
        leading = { IconBadge(icon = DesktopIcons.fileFor(entry.name, entry.isDirectory)) },
        trailing = {
            Box {
                IconButton(onClick = onOpenMenu) {
                    DesktopIcon(
                        icon = DesktopIcons.overflow,
                        size = 24.dp,
                        contentDescription = stringResource(R.string.files_action_more),
                    )
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = onCloseMenu) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_rename)) },
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.rename, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestRename(entry)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_copy)) },
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.copy, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestTransfer(entry, move = false)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_move)) },
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.move, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestTransfer(entry, move = true)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.common_delete)) },
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.delete, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestDelete(entry)
                        },
                    )
                }
            }
        },
        onClick = onOpen,
    )
}

/**
 * Properties of the selected entry.
 *
 * [onBack] is `null` when the caller is rendering this as a pane; a pane has nothing to go back from,
 * and a visible-but-inert back button would misdescribe the layout.
 */
@Composable
fun FileDetailScreen(
    viewModel: FilesViewModel,
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val entry = viewModel.selected
    val properties = viewModel.properties

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        if (entry == null) {
            ScreenHeader(title = stringResource(R.string.files_detail_title), onBack = onBack)
            EmptyState(
                text = stringResource(R.string.files_detail_none),
                icon = DesktopIcons.notice,
            )
            return@Column
        }

        ScreenHeader(
            title = stringResource(R.string.files_detail_title),
            onBack = onBack,
        )

        SectionCard(
            title = entry.name,
            leading = DesktopIcons.fileFor(entry.name, entry.isDirectory),
        ) {
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
                Text(
                    stringResource(
                        if (viewModel.propertiesLoading) R.string.common_loading else R.string.files_detail_unavailable,
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            if (!entry.isDirectory) {
                Button(
                    onClick = { viewModel.download(entry) },
                    enabled = viewModel.transfer == null,
                    modifier = Modifier.weight(1f),
                ) {
                    DesktopIcon(icon = DesktopIcons.download, size = 18.dp)
                    Spacer(Modifier.width(Spacing.sm))
                    Text(stringResource(R.string.files_action_download))
                }
            }
            OutlinedButton(onClick = { viewModel.requestRename(entry) }, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.files_action_rename))
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            TextButton(onClick = { viewModel.requestTransfer(entry, move = false) }) {
                Text(stringResource(R.string.files_action_copy))
            }
            TextButton(onClick = { viewModel.requestTransfer(entry, move = true) }) {
                Text(stringResource(R.string.files_action_move))
            }
            TextButton(
                onClick = { viewModel.requestDelete(entry) },
                colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
            ) { Text(stringResource(R.string.common_delete)) }
        }
    }
}

/** Shared overlay for both list and detail routes, so compact detail actions never become inert. */
@Composable
fun FileOperationOverlays(viewModel: FilesViewModel) {
    if (viewModel.newDirectoryOpen) {
        NewDirectoryDialog(
            parentPath = viewModel.path,
            onDismiss = viewModel::cancelNewDirectory,
            onConfirm = viewModel::confirmNewDirectory,
        )
    }
    viewModel.renameTarget?.let { entry ->
        RenameDialog(entry, onDismiss = viewModel::cancelRename, onConfirm = viewModel::confirmRename)
    }
    viewModel.transferTarget?.let { request ->
        TransferDialog(
            request = request,
            onDismiss = viewModel::cancelTransferTarget,
            onConfirm = viewModel::confirmTransfer,
        )
    }
    viewModel.deleteTarget?.let { entry ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.files_delete_title),
            message = stringResource(R.string.files_delete_message, entry.path),
            confirmLabel = stringResource(R.string.common_delete),
            busy = viewModel.loading,
            onConfirm = viewModel::confirmDelete,
            onDismiss = viewModel::cancelDelete,
        )
    }
}

@Composable
private fun NewDirectoryDialog(parentPath: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.files_new_directory_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.files_new_directory_body, parentPath.ifBlank { "/" }))
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.files_label_name)) },
                    shape = MaterialTheme.shapes.medium,
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
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(entry.path)
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.files_label_name)) },
                    shape = MaterialTheme.shapes.medium,
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

@Composable
private fun TransferDialog(request: TransferTarget, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var destination by remember { mutableStateOf(request.entry.path) }
    val title = stringResource(if (request.move) R.string.files_move_title else R.string.files_copy_title)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.files_transfer_body, request.entry.path))
                OutlinedTextField(
                    value = destination,
                    onValueChange = { destination = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.files_destination_path)) },
                    shape = MaterialTheme.shapes.medium,
                )
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(destination.trim()) }, enabled = destination.isNotBlank() && destination != request.entry.path) {
                Text(stringResource(if (request.move) R.string.files_action_move else R.string.files_action_copy))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}
