package app.relaxkonos.mobile.ui.files

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import app.relaxkonos.mobile.ui.common.ActionLabel
import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Checkbox
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.data.FileBrowserPolicy
import app.relaxkonos.mobile.data.TransferKind
import app.relaxkonos.mobile.data.UploadFailure
import app.relaxkonos.mobile.data.UploadStage
import app.relaxkonos.mobile.data.UploadState
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ProgressSheet
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.common.RemotePathPicker
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

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
    val pickUpload = rememberLauncherForActivityResult(rememberUsageOpenDocument("FilesScreen.upload")) { uri ->
        uri?.let(viewModel::upload)
    }

    // Asked for only once an upload is actually running, and never before it starts: the permission is
    // what makes the transfer's cancel action visible, so the moment the user has a transfer to cancel is
    // the only moment the question has a reason. A refusal costs the notification, not the upload.
    val requestUploadNotifications = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { viewModel.uploadNotificationPromptHandled() }

    LaunchedEffect(viewModel.uploadNotificationPrompt) {
        if (viewModel.uploadNotificationPrompt) {
            requestUploadNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    LaunchedEffect(Unit) { viewModel.start() }

    // Only one upload runs at a time, so the picker stays closed while either route is busy. The
    // resumable route is not awaited by this page, which is why its running state comes from the
    // coordinator's flow rather than from `transfer`.
    val uploadRunning = viewModel.uploadState.collectAsStateValue()?.isRunning == true

    var menuForPath by remember { mutableStateOf<String?>(null) }
    var locationOpen by remember { mutableStateOf(false) }
    if (locationOpen) FileLocationDialog(viewModel) { locationOpen = false }

    Column(
        modifier = modifier.fillMaxSize().padding(horizontal = Spacing.md, vertical = Spacing.sm),
        verticalArrangement = Arrangement.spacedBy(Spacing.xs),
    ) {
        ScreenHeader(title = stringResource(R.string.nav_files))

        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            FilledTonalIconButton(onClick = { viewModel.goUp() }, enabled = viewModel.canGoUp && !viewModel.batchRunning && !viewModel.mutationBusy) {
                DesktopIcon(
                    icon = DesktopIcons.parentDirectory,
                    size = 24.dp,
                    contentDescription = stringResource(R.string.files_action_up),
                )
            }
            TextButton(onClick = { locationOpen = true }, modifier = Modifier.weight(1f),
                enabled = !viewModel.batchRunning && !viewModel.mutationBusy) {
                LocationBar(path = viewModel.path.ifBlank { stringResource(R.string.files_root) }, modifier = Modifier.fillMaxWidth())
            }
            IconButton(onClick = { viewModel.refresh() }) {
                DesktopIcon(
                    icon = DesktopIcons.refresh,
                    size = 24.dp,
                    contentDescription = stringResource(R.string.common_refresh),
                )
            }
        }

        FileBrowserControls(viewModel, onLocation = { locationOpen = true },
            onUpload = { pickUpload.launch(arrayOf("*/*")) }, uploadRunning = uploadRunning)

        // Above the list, not below it: an unfinished upload is a thing the user came here to act on,
        // and a card that only appears once every entry has been scrolled past is a card nobody sees.
        FileResumableUploadsCard(viewModel)

        val listing = viewModel.listing
        val visible = viewModel.visibleEntries
        if (listing == null || visible.isEmpty()) {
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
                items(visible, key = { it.path }) { entry ->
                    FileEntryRow(
                        entry = entry,
                        menuOpen = menuForPath == entry.path,
                        onOpenMenu = { menuForPath = entry.path },
                        onCloseMenu = { menuForPath = null },
                        onOpen = {
                            if (viewModel.selectionMode) {
                                viewModel.check(entry)
                            } else if (entry.isDirectory) {
                                viewModel.open(entry.path)
                            } else {
                                viewModel.select(entry)
                                onOpenDetail()
                            }
                        },
                        viewModel = viewModel,
                        onDetails = { viewModel.select(entry); onOpenDetail() },
                    )
                }
            }
        }
    }
}

/**
 * The message banner for everything the files destination reports.
 *
 * It is rendered by the destination rather than by a screen because a transfer and a failed
 * navigation both outlive the page that started them: the Compact and Medium layouts pop the list out
 * of the composition while the file detail is shown, so a banner owned by the list would surface a
 * result only on the way back — long after the moment it describes.
 */
@Composable
fun FileActionFeedback(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    viewModel.message?.let { banner ->
        ActionFeedback(
            message = banner,
            onRetry = { viewModel.refresh() },
            onDismiss = { viewModel.dismissMessage() },
            modifier = modifier.padding(horizontal = Spacing.lg, vertical = Spacing.sm),
        )
    }
}

/**
 * The progress card for the transfer in flight, pinned below the files content.
 *
 * Like the banner, it belongs to the destination: a download started from the detail page is still
 * running when that page is popped, and a card that left with it would be the only sign the user ever
 * gets that anything happened. The card is collapsible and never blocks navigation
 * (`Shell.Design.md` §3.4).
 */
@Composable
fun FileTransferCard(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    if (viewModel.batchRunning) {
        ProgressSheet(title = stringResource(R.string.files_batch_running), detail = viewModel.batchPath,
            progress = viewModel.batchProgress.toFloat() / viewModel.batchTotal.coerceAtLeast(1), collapsed = false,
            onCollapsedChange = {}, onCancel = if (viewModel.batchStopRequested) null else viewModel::stopBatch,
            footnote = stringResource(if (viewModel.batchStopRequested) R.string.files_batch_stopping else R.string.files_batch_stop_hint),
            modifier = modifier.padding(horizontal = Spacing.lg, vertical = Spacing.sm))
        return
    }
    val transfer = viewModel.transfer ?: return
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
        modifier = modifier.padding(horizontal = Spacing.lg, vertical = Spacing.sm),
    )
}

/**
 * The card for the resumable upload in flight, or the last one that stopped.
 *
 * It sits beside [FileTransferCard] for the same reason that one does — a multi-gigabyte transfer
 * outlives the page that started it — but it is a card of its own because it answers a different
 * question. A transfer card reports bytes; this one also reports that the transfer is stoppable *and
 * continuable*, which is the entire point of the resumable protocol. A stopped upload has deliberately
 * kept its session, its resume entry and its cache copy, so the card offers "continue" and "discard"
 * instead of pretending the transfer is still running or offering a "retry" that would quietly start
 * again from zero.
 *
 * [UploadStage.Preparing] is drawn as a stage of its own and never as the first few percent: while a
 * document is being copied into the cache nothing has left the device, and a bar that advanced would
 * claim otherwise. For the same reason a stopped transfer's bar is frozen where it stopped rather than
 * left indeterminate, which would read as "still working".
 */
@Composable
fun FileUploadCard(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    val state = viewModel.uploadState.collectAsStateValue() ?: return
    // A finished upload is reported through the process-wide notice, which outlives this page.
    if (state.isFinished) return
    val resumable = viewModel.resumableUploads.collectAsStateValue()
    val stopped = state.failure != null
    // The entry is looked up by session id rather than by file name: two uploads of the same name are
    // two different sessions, and "continue" has to resume the one the failure belongs to.
    val entry = state.uploadId?.let { id -> resumable.firstOrNull { it.uploadId == id } }
    val actions: (@Composable RowScope.() -> Unit)? =
        if (!stopped) {
            null
        } else {
            {
                if (entry != null) {
                    Button(
                        onClick = { viewModel.resumeUpload(entry) },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.files_upload_resume))
                    }
                    OutlinedButton(
                        onClick = { viewModel.discardUpload(entry) },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.files_upload_discard))
                    }
                } else {
                    // Nothing is left to continue — a session the server refused outright, or a document
                    // that could not even be staged. Dismissing is the only honest action.
                    OutlinedButton(
                        onClick = viewModel::dismissUpload,
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.common_dismiss))
                    }
                }
            }
        }
    val content: @Composable () -> Unit = {
        ProgressSheet(
            title = stringResource(uploadTitle(state)),
            detail = state.fileName,
            progress = when {
                stopped -> state.progress ?: 0f
                state.stage == UploadStage.Preparing -> null
                else -> state.progress ?: 0f
            },
            collapsed = viewModel.uploadCollapsed,
            onCollapsedChange = viewModel::setUploadCollapsedState,
            onCancel = if (stopped) null else viewModel::cancelUpload,
            modifier = modifier.padding(horizontal = Spacing.lg, vertical = Spacing.sm),
            footnote = when {
                stopped -> stringResource(uploadFailureText(state.failure!!))
                state.resynchronising -> stringResource(R.string.files_upload_reconciling)
                state.totalBytes != null -> stringResource(
                    R.string.files_upload_progress,
                    formatSize(state.displayedBytes) ?: "",
                    formatSize(state.totalBytes) ?: "",
                ) + (state.progress?.let { " · ${(it * 100).toInt()}%" } ?: "")

                else -> null
            },
            actions = actions,
        )
    }
    content()
}

/**
 * The unfinished uploads this device can still continue.
 *
 * Belongs with the file list rather than beside the transfer card, because it is content and not a
 * receipt: a resume entry outlives the process, so it has to be reachable by opening the files
 * destination rather than by being on screen at the right moment. The transfer currently on the card is
 * filtered out so the same session is never offered twice.
 */
@Composable
fun FileResumableUploadsCard(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    val entries = viewModel.resumableUploads.collectAsStateValue()
    val active = viewModel.uploadState.collectAsStateValue()?.uploadId
    val pending = entries.filterNot { it.uploadId == active }
    if (pending.isEmpty()) return
    SectionCard(
        title = stringResource(R.string.files_upload_resumable),
        leading = DesktopIcons.upload,
        modifier = modifier,
    ) {
        for (entry in pending) {
            ListRow(
                title = entry.fileName,
                supporting = listOfNotNull(
                    entry.targetDirectoryPath,
                    formatSize(entry.confirmedOffset),
                    formatSize(entry.totalLength),
                ).joinToString(" · "),
                leading = { IconBadge(DesktopIcons.fileFor(entry.fileName, false)) },
                trailing = {
                    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        OutlinedButton(onClick = { viewModel.discardUpload(entry) }) {
                            Text(stringResource(R.string.files_upload_discard))
                        }
                        Button(onClick = { viewModel.resumeUpload(entry) }) {
                            Text(stringResource(R.string.files_upload_resume))
                        }
                    }
                },
            )
        }
    }
}

@StringRes
private fun uploadTitle(state: UploadState): Int = when {
    state.failure != null -> R.string.files_upload_resumable
    state.stage == UploadStage.Preparing -> R.string.files_upload_preparing
    state.stage == UploadStage.Committing -> R.string.files_upload_committing
    else -> R.string.files_uploading
}

/** One sentence per failure, in terms of what the user has to do about it. */
@StringRes
private fun uploadFailureText(failure: UploadFailure): Int = when (failure) {
    UploadFailure.Network -> R.string.files_upload_failed_network
    UploadFailure.ServerStorage -> R.string.files_upload_failed_server_storage
    UploadFailure.LocalCache -> R.string.files_upload_failed_local_cache
    UploadFailure.SourceChanged -> R.string.files_upload_failed_source_changed
    UploadFailure.SourceUnreadable -> R.string.files_upload_failed_source_unreadable
    UploadFailure.ElevationRequired -> R.string.files_upload_failed_elevation
    UploadFailure.SessionLost -> R.string.files_upload_failed_session
    UploadFailure.NameUnusable -> R.string.files_upload_failed_name
    UploadFailure.Server -> R.string.files_upload_failed_server
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
    onDetails: () -> Unit,
) {
    ListRow(
        title = entry.name,
        supporting = listOfNotNull(
            stringResource(if (entry.isDrive) R.string.files_kind_drive else if (entry.isDirectory) R.string.files_kind_directory else R.string.files_kind_file),
            formatSize(entry.sizeBytes),
            formatTimestamp(entry.modifiedAtMillis),
        ).joinToString(" · "),
        leading = { IconBadge(icon = if (entry.isDrive) DesktopIcons.storage else DesktopIcons.fileFor(entry.name, entry.isDirectory)) },
        selected = entry.path in viewModel.checkedPaths || entry.path == viewModel.selected?.path,
        trailing = {
            if (viewModel.selectionMode) {
                Checkbox(checked = entry.path in viewModel.checkedPaths,
                    onCheckedChange = { viewModel.check(entry) },
                    enabled = !viewModel.batchRunning && FileBrowserPolicy.mutable(entry))
            } else Box {
                IconButton(onClick = onOpenMenu, enabled = !viewModel.batchRunning && !viewModel.mutationBusy) {
                    DesktopIcon(
                        icon = DesktopIcons.overflow,
                        size = 24.dp,
                        contentDescription = stringResource(R.string.files_action_more),
                    )
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = onCloseMenu) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_detail_title)) },
                        onClick = { onCloseMenu(); onDetails() })
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_rename)) },
                        enabled = viewModel.canMutate && FileBrowserPolicy.mutable(entry),
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.rename, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestRename(entry)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_copy)) },
                        enabled = viewModel.canMutate && FileBrowserPolicy.mutable(entry),
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.copy, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestTransfer(entry, move = false)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_move)) },
                        enabled = viewModel.canMutate && FileBrowserPolicy.mutable(entry),
                        leadingIcon = { DesktopIcon(icon = DesktopIcons.move, size = 20.dp) },
                        onClick = {
                            onCloseMenu()
                            viewModel.requestTransfer(entry, move = true)
                        },
                    )
                    DropdownMenuItem(
                        text = { ActionLabel(R.string.common_delete) },
                        enabled = viewModel.canMutate && FileBrowserPolicy.mutable(entry),
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
 * Properties of the selected entry, and its picture when it holds one.
 *
 * [onBack] is `null` when the caller is rendering this as a pane; a pane has nothing to go back from,
 * and a visible-but-inert back button would misdescribe the layout.
 *
 * The content scrolls, and that is not a detail: a preview was added above the facts, and on a phone
 * held sideways the two together are taller than the window. The header stays outside the scrolling
 * area so the way back never leaves the screen.
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

        Column(
            modifier = Modifier.weight(1f).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(Spacing.lg),
        ) {
            FileImagePreview(viewModel)
            if (entry.isDirectory) OutlinedButton(onClick = { viewModel.open(entry.path) }) {
                Text(stringResource(R.string.files_open_directory))
            }
            if (!entry.isDirectory) OutlinedButton(onClick = { viewModel.editText(entry.path) }, enabled = !viewModel.batchRunning && !viewModel.mutationBusy) {
                Text(stringResource(R.string.editor_open))
            }

            SectionCard(
                title = entry.name,
                leading = if (entry.isDrive) DesktopIcons.storage else DesktopIcons.fileFor(entry.name, entry.isDirectory),
            ) {
                KeyValueRow(stringResource(R.string.files_label_path), entry.path)
                KeyValueRow(
                    stringResource(R.string.files_label_kind),
                    stringResource(if (entry.isDrive) R.string.files_kind_drive else if (entry.isDirectory) R.string.files_kind_directory else R.string.files_kind_file),
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
                formatTimestamp(properties?.accessedMillis)?.let {
                    KeyValueRow(stringResource(R.string.files_label_accessed), it)
                }
                properties?.attributes?.takeIf { it.isNotBlank() }?.let {
                    KeyValueRow(stringResource(R.string.files_label_attributes), it)
                }
                properties?.permissions?.takeIf { it.isNotBlank() }?.let {
                    KeyValueRow(stringResource(R.string.files_label_permissions), it)
                }
                if (container.capabilities.contains(ServerCapabilities.POSIX_PERMISSIONS) && properties?.unixMode != null) {
                    KeyValueRow(stringResource(R.string.files_permissions_octal), FileBrowserPolicy.formatMode(properties.unixMode))
                    OutlinedButton(onClick = viewModel::openPermissions, enabled = viewModel.canMutate) {
                        Text(stringResource(R.string.files_permissions_edit))
                    }
                }
                if (properties == null) {
                    TextButton(onClick = { viewModel.reloadProperties(viewModel.propertiesNeedsElevation) }, enabled = !viewModel.propertiesLoading) {
                        Text(stringResource(if (viewModel.propertiesNeedsElevation) R.string.files_preview_authorize else R.string.common_retry))
                    }
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
                ) { ActionLabel(R.string.common_delete) }
            }
        }
    }
}

/** Shared overlay for both list and detail routes, so compact detail actions never become inert. */
@Composable
fun FileOperationOverlays(viewModel: FilesViewModel) {
    val owner = appContainer().session.state.value as? SessionState.Active
    FileBatchOverlays(viewModel)
    FilePermissionDialog(viewModel)
    if (viewModel.editorOpen && owner != null) app.relaxkonos.mobile.ui.editor.TextEditorDialog(
        owner, viewModel.editorPath, onSaved = { viewModel.refresh() }, onClose = { viewModel.editorOpen = false })
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
    // The viewer covers the whole window, so it belongs to the destination rather than to the detail
    // pane that opened it: a re-layout that unmounts that pane must not take the picture with it.
    ImagePreviewViewer(viewModel)
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
            Button(onClick = { onConfirm(name) }, enabled = FileBrowserPolicy.validName(name)) {
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
            Button(onClick = { onConfirm(name) }, enabled = FileBrowserPolicy.validName(name) && name != entry.name) {
                ActionLabel(R.string.common_save)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
private fun TransferDialog(request: TransferTarget, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var destination by remember { mutableStateOf(request.entry.path) }
    var browsing by remember { mutableStateOf(false) }
    val files = appContainer().files
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
                OutlinedButton(onClick = { browsing = true }) {
                    Text(stringResource(R.string.remote_path_browse))
                }
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(destination) }, enabled = destination.isNotBlank() && destination != request.entry.path) {
                Text(stringResource(if (request.move) R.string.files_action_move else R.string.files_action_copy))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
    if (browsing) RemotePathPicker(
        kind = RemotePathKind.Directory,
        initialPath = files.navigationParentOf(destination),
        title = R.string.files_destination_path,
        onDismiss = { browsing = false },
        onSelect = { directory ->
            destination = files.childOf(directory, request.entry.name)
            browsing = false
        },
    )
}
