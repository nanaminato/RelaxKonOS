package app.relaxkonos.mobile.ui.files

import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.UploadProblemCodes
import app.relaxkonos.mobile.data.DownloadTarget
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.data.PickedDocument
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.data.UploadFailure
import app.relaxkonos.mobile.data.UploadResumeEntry
import app.relaxkonos.mobile.data.UploadStage
import app.relaxkonos.mobile.data.UploadState
import app.relaxkonos.mobile.data.isDecodableImage
import app.relaxkonos.mobile.data.isSingleShotLength
import app.relaxkonos.mobile.service.UploadForegroundService
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ProgressSheet
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.common.withDebugDetail
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import java.io.File
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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
 * The edge, in pixels, of the first decode of an image, done here on the file that has just landed.
 *
 * Small enough that the decode is free no matter how large the picture is, and large enough that the
 * blur it shows is recognisably the picture rather than a smear of colours.
 */
private const val LOCAL_THUMBNAIL_EDGE_PX = 96

/**
 * The longest edge, in pixels, asked of the server for its own small copy of the picture.
 *
 * Larger than [LOCAL_THUMBNAIL_EDGE_PX] because this one arrives *instead of* the file rather than
 * after it: it is the only thing on screen while a whole photograph is still transferring, and it is
 * drawn in a box 220 dp tall — 320 px is that box on the densities phones actually have, near enough
 * that the picture reads as itself, while the answer stays tens of kilobytes rather than megabytes.
 * A local decode on top of it would add nothing.
 */
private const val SERVER_THUMBNAIL_EDGE_PX = 320

/**
 * The box a decode assumes before a screen has measured itself.
 *
 * Only ever used for the moment between selecting a file and the pane reporting its size, and rounded
 * up on purpose: a decode for a box that turns out to be smaller is wasted memory, but one for a box
 * that turns out to be larger is a visibly soft picture.
 */
private val DEFAULT_PREVIEW_BOX = IntSize(1080, 1080)

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

    var transfer by mutableStateOf<Transfer?>(null)
        private set

    private var transferJob: Job? = null

    /** Uploads this device can still continue, as the coordinator sees them. */
    val resumableUploads: StateFlow<List<UploadResumeEntry>> get() = container.uploads.resumable

    /** The resumable transfer in flight or the last one that stopped. */
    val uploadState: StateFlow<UploadState?> get() = container.uploads.state

    /** Whether the resumable upload card is showing its detail, kept on the page like [transfer]'s. */
    var uploadCollapsed by mutableStateOf(false)
        private set

    /**
     * Named `…State` rather than `setUploadCollapsed` because the property above already generates a
     * private setter with that exact JVM signature, and the two would be a platform declaration clash.
     */
    fun setUploadCollapsedState(collapsed: Boolean) {
        uploadCollapsed = collapsed
    }

    /** Set when an upload needs the notification permission asked for; cleared once the prompt has run. */
    var uploadNotificationPrompt by mutableStateOf(false)
        private set

    fun uploadNotificationPromptHandled() {
        uploadNotificationPrompt = false
    }

    /**
     * The finished-upload report.
     *
     * A resumable upload is not awaited by any screen, so its outcome has to be *observed*. It is
     * announced through the process-wide notice rather than this page's banner, because the transfer
     * routinely finishes while the files route is somewhere behind the user: a banner owned by the page
     * would surface the result only on the way back, or never.
     */
    private var reportedUpload: UploadState? = null

    init {
        viewModelScope.launch {
            container.uploads.state.collect { state ->
                if (state == null || state === reportedUpload) return@collect
                if (!state.isFinished) return@collect
                reportedUpload = state
                container.showNotice(
                    UiMessage(R.string.files_uploaded, listOf(state.fileName), tone = StatusTone.Success),
                )
                container.recentOperations.record(RecentOperationKind.Upload, state.fileName)
                container.uploads.dismiss()
                reload()
            }
        }
    }

    /** What the detail pane shows above the properties; see `ImagePreview`. */
    var preview by mutableStateOf<ImagePreview>(ImagePreview.Hidden)
        private set

    /** Whether the full-screen viewer is open. It draws the same cached bytes at the screen's size. */
    var viewerOpen by mutableStateOf(false)
        private set

    private var previewJob: Job? = null

    /**
     * The decode of a larger box, kept apart from [previewJob] because it never touches the network:
     * cancelling it costs nothing, and it must not be able to cancel the transfer.
     */
    private var previewUpgradeJob: Job? = null

    /**
     * Bumped whenever a newer preview takes over the pane.
     *
     * A cancelled job is not a stopped job: cancelling a transfer does not interrupt a blocking read,
     * so the old download keeps writing and reaches its epilogue afterwards. Every write a job makes —
     * to [preview], to the cache, to the screen — is therefore guarded by "is my generation still the
     * current one", which is what keeps the previous selection's image from landing on top of the new
     * one's a moment after the user taps something else.
     */
    private var previewGeneration = 0

    /** The cached file the current preview came from, once something has been decoded from it. */
    private var previewFile: File? = null

    /** The box the bitmap on screen was decoded for, so a larger box can be recognised as an upgrade. */
    private var previewDecodedFor = IntSize.Zero

    /** The box the next decode should use; reported by whatever is drawing the preview. */
    private var previewBounds = DEFAULT_PREVIEW_BOX

    /**
     * The fetch of the server's small copy of the current picture, while one is running.
     *
     * It is kept apart from [previewJob] because nothing depends on it: it is never awaited, so
     * cancelling it can never cancel a transfer, and a fetch that is still running when the picture
     * itself arrives has simply outlived its usefulness.
     */
    private var thumbnailJob: Job? = null

    /**
     * The server's small copy of the current picture, once it has arrived.
     *
     * Held rather than used and dropped, because it is drawn twice: under the progress bar while the
     * transfer runs, and afterwards as the placeholder the full decode sits behind. It also has to
     * survive a race — a thumbnail that lands before the transfer has published its first byte must
     * still be there when that byte is announced.
     */
    private var thumbnail: Bitmap? = null

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
        // Leaving a directory also leaves the entry the detail pane was describing, preview included.
        select(null)
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

    /**
     * Shows the properties of [entry] — and, when it is an image, the image itself.
     *
     * The preview starts by itself: a user who taps a picture in a file list is asking to see it, and
     * making them ask twice would be a worse answer than fetching it. What it will not do is ask for
     * anything on its own — an image behind a path the account cannot read reports that it needs
     * authorization instead of raising a password prompt nobody requested.
     */
    fun select(entry: RemoteEntry?) {
        // Whatever was loading belonged to the previous selection and is worthless now.
        cancelPreview()
        selected = entry
        properties = null
        preview = ImagePreview.Hidden
        previewFile = null
        previewDecodedFor = IntSize.Zero
        thumbnail = null
        viewerOpen = false
        if (entry == null) {
            return
        }
        loadProperties(entry.path)
        if (entry.isDecodableImage()) {
            loadPreview(entry, authorize = false)
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
                    message = UiMessage(R.string.files_created, listOf(target), tone = StatusTone.Success)
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

    /**
     * Streams a remote file into this device's Downloads.
     *
     * The transfer is owned by the ViewModel and by nothing else. It must not need a screen to be
     * mounted to finish or to be reported: in the Compact and Medium layouts the file detail is a
     * pushed page, so the list is out of the composition while the download runs, and anything the
     * download needs — the progress card, the result message, the confirmation — has to come from
     * somewhere that outlives both routes (`FileTransferCard`, `FileMessageBanner`).
     */
    fun download(entry: RemoteEntry) {
        if (transfer != null) {
            return
        }
        transfer = Transfer(label = entry.path, kind = TransferKind.Download)
        transferJob = viewModelScope.launch {
            var target: DownloadTarget? = null
            try {
                // Creating the destination is a write into someone else's storage (a `MediaStore`
                // insert, or a directory that has to be made). It happens off the main thread, and
                // after the card is already on screen, so the tap is acknowledged before the first
                // byte and without a stalled frame.
                val created = withContext(Dispatchers.IO) { runCatching { container.downloads.create(entry.name) } }
                if (created.isFailure) {
                    message = UiMessage(R.string.files_download_failed).withDebugDetail(created.exceptionOrNull()?.message)
                    return@launch
                }
                val destination = created.getOrThrow()
                target = destination
                val result = container.files.download(entry.path, destination, container.elevationAnswers) { written, total ->
                    viewModelScope.launch(Dispatchers.Main.immediate) {
                        transfer = transfer?.takeIf { it.kind == TransferKind.Download }?.copy(
                            transferredBytes = written,
                            totalBytes = total,
                        )
                    }
                }
                when (result) {
                    is ApiResult.Success -> {
                        // Pending until this call: the file becomes visible to the rest of the device
                        // only once it is complete. A commit the platform refuses means the bytes are
                        // not actually in Downloads, so it is reported as a failure rather than
                        // followed by a success message that would not be true.
                        val committed = withContext(Dispatchers.IO) { runCatching { destination.commit() } }
                        if (committed.isSuccess) {
                            target = null
                            container.recentOperations.record(RecentOperationKind.Download, entry.path)
                            message = UiMessage(R.string.files_downloaded, listOf(destination.location), tone = StatusTone.Success)
                        } else {
                            message = UiMessage(R.string.files_download_failed).withDebugDetail(committed.exceptionOrNull()?.message)
                        }
                    }
                    else -> message = result.failureMessage()
                }
            } catch (_: CancellationException) {
                // Expected when the user cancels the transfer.
            } finally {
                // A refused, failed or cancelled transfer leaves no file: on the shared Downloads
                // collection an abandoned row would be a corrupt entry for the whole device to see.
                target?.let { runCatching { it.discard() } }
                transfer = null
                transferJob = null
            }
        }
    }

    /**
     * Starts an upload of one picked document.
     *
     * Which route it takes is decided by its declared length. A photo or a PDF stays on the single
     * request route, because paying for a session would only be slower. Anything past the threshold goes
     * through the app-scoped [UploadCoordinator], under a foreground service: a multi-gigabyte transfer
     * must not be tied to this page, and it has to be continuable after the process is killed.
     *
     * The destination directory is captured before anything is opened, so navigating while a large upload
     * runs cannot move the file to wherever the user ended up.
     */
    fun upload(uri: Uri) {
        if (isUploadBusy()) return
        val directory = path
        if (directory.isBlank()) {
            message = UiMessage(R.string.files_upload_needs_folder)
            return
        }
        transferJob = viewModelScope.launch {
            // Reading a document's metadata and opening it are the provider's work — a cloud-backed file
            // can take seconds — so neither happens on the main thread.
            val document = withContext(Dispatchers.IO) {
                runCatching { container.uploadDocuments.open(uri.toString()) }.getOrNull()
            }
            if (document == null) {
                message = UiMessage(R.string.files_upload_unreadable)
                transferJob = null
                return@launch
            }
            val length = document.length
            if (length != null && isSingleShotLength(length)) {
                uploadSingleShot(document, directory)
            } else {
                startResumable(document, directory)
            }
            transferJob = null
        }
    }

    /** True while either route is busy; only one transfer runs at a time. */
    private fun isUploadBusy(): Boolean = transfer != null || container.uploads.isRunning

    /**
     * Hands the document to the resumable coordinator.
     *
     * Nothing here is awaited: the transfer is the coordinator's, so the card keeps rendering after this
     * ViewModel is gone and the transfer survives the page. The foreground service is what keeps Android
     * from freezing the process the moment the user leaves the app.
     */
    private fun startResumable(document: PickedDocument, directory: String) {
        transfer = null
        container.uploads.start(directory, document)
        // Started before the prompt, not after: a foreground service has to be up within seconds, and the
        // permission request must never be a precondition for the transfer the user asked for.
        UploadForegroundService.start(getApplication())
        if (needsNotificationPermission()) uploadNotificationPrompt = true
    }

    /**
     * Whether the transfer's notification would be invisible without asking.
     *
     * Android 13 suppresses foreground-service notifications when `POST_NOTIFICATIONS` has not been
     * granted, which takes the cancel action with it and leaves a running upload the user can only stop
     * from inside the app. The flag is raised here and the request is made by the composition, because
     * only an activity can ask.
     */
    private fun needsNotificationPermission(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            getApplication<Application>().checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED

    /**
     * Copies a small document to the server in one request.
     *
     * A declared length is a scheduling hint, not a contract: a provider that under-reports its size — or
     * refuses to report one — is answered by the server with `upload-too-large-for-single-shot`, and that
     * specific refusal falls through to the resumable route rather than failing the upload.
     */
    private suspend fun uploadSingleShot(document: PickedDocument, directory: String) {
        val app = getApplication<RelaxKonApplication>()
        val name = document.displayName.ifBlank { app.getString(R.string.files_upload_default_name) }
        transfer = Transfer(label = name, kind = TransferKind.Upload, totalBytes = document.length)
        val result = runCatching {
            withContext(Dispatchers.IO) { document.open() }.use { input ->
                container.files.upload(directory, name, input, document.length, container.elevationAnswers) { written ->
                    viewModelScope.launch(Dispatchers.Main.immediate) {
                        transfer = transfer?.takeIf { it.kind == TransferKind.Upload }?.copy(transferredBytes = written)
                    }
                }
            }
        }.getOrElse {
            if (it is CancellationException) throw it
            ApiResult.Transport(it.message)
        }
        transfer = null
        when (result) {
            is ApiResult.Success -> {
                message = UiMessage(R.string.files_uploaded, listOf(name), tone = StatusTone.Success)
                container.recentOperations.record(RecentOperationKind.Upload, name)
                reload()
            }

            is ApiResult.Problem if result.code == UploadProblemCodes.TOO_LARGE_FOR_SINGLE_SHOT ->
                startResumable(document, directory)

            else -> message = result.failureMessage()
        }
    }

    /** Continues an unfinished upload the coordinator remembers. */
    fun resumeUpload(entry: UploadResumeEntry) {
        if (isUploadBusy()) return
        container.uploads.resume(entry)
        UploadForegroundService.start(getApplication())
        if (needsNotificationPermission()) uploadNotificationPrompt = true
    }

    /** Abandons an unfinished upload: session, resume entry and cache copy all go. */
    fun discardUpload(entry: UploadResumeEntry) = container.uploads.discard(entry)

    /** Stops the resumable upload in flight and abandons it. */
    fun cancelUpload() = container.uploads.cancel()

    /** Clears a stopped upload card that has no session left to continue. */
    fun dismissUpload() = container.uploads.dismiss()

    fun cancelActiveTransfer() {
        transferJob?.cancel()
        transferJob = null
        transfer = null
    }

    fun setTransferCollapsed(collapsed: Boolean) {
        transfer = transfer?.copy(collapsed = collapsed)
    }

    /** Opens the full-screen viewer for the image already on screen; there is nothing to load first. */
    fun openViewer() {
        if (preview is ImagePreview.Ready) {
            viewerOpen = true
        }
    }

    fun closeViewer() {
        viewerOpen = false
    }

    /**
     * Runs the preview again: after a failure, or after the user has authorized a protected file.
     *
     * [authorize] is what separates the two: only a tap on the card's own authorization button may
     * raise the elevation prompt, which is the same answer-the-dialog-or-decline contract every other
     * file operation follows (`RelaxKonOS.Mobile.V1.Design.md` §5.3.8).
     */
    fun reloadPreview(authorize: Boolean) {
        val entry = selected ?: return
        cancelPreview()
        preview = ImagePreview.Hidden
        previewFile = null
        previewDecodedFor = IntSize.Zero
        thumbnail = null
        loadPreview(entry, authorize)
    }

    /**
     * Records the pixel size of the box the preview is drawn in.
     *
     * Called by whatever is on screen — the card, and the viewer once it opens — because that
     * measurement, not a constant, is what decides how much of the picture is worth decoding. Quality
     * only ever goes up within one selection: a box that wants more pixels triggers a re-decode from the
     * cache, and one that wants fewer is ignored, so closing the viewer does not throw away the decode
     * that was just made.
     */
    fun setPreviewBounds(widthPx: Int, heightPx: Int) {
        val box = if (widthPx > 0 && heightPx > 0) IntSize(widthPx, heightPx) else DEFAULT_PREVIEW_BOX
        previewBounds = box
        val file = previewFile ?: return
        // Below a quarter more on both axes the difference would not be visible; re-decoding for it
        // would only make the pane flicker.
        if (box.width * 4 < previewDecodedFor.width * 5 && box.height * 4 < previewDecodedFor.height * 5) {
            return
        }
        upgradePreview(file, box)
    }

    /** Invalidates whatever is loading and stops it from publishing anything further. */
    private fun cancelPreview() {
        previewJob?.cancel()
        previewJob = null
        previewUpgradeJob?.cancel()
        previewUpgradeJob = null
        // Cancelling this one costs nothing and can break nothing: nothing awaits it, and a thumbnail
        // of the previous selection has no meaning for the new one.
        thumbnailJob?.cancel()
        thumbnailJob = null
        previewGeneration++
    }

    /**
     * Fetches the selected image unless a copy is already cached, then decodes it.
     *
     * The caller must have bumped [previewGeneration] first: the generation captured here is what its
     * every write is checked against.
     */
    private fun loadPreview(entry: RemoteEntry, authorize: Boolean) {
        val generation = previewGeneration
        previewJob = viewModelScope.launch {
            val scope = container.activeSession?.serviceId.orEmpty()
            // Reading the cache is stat calls and, on a miss, a directory listing followed by an
            // eviction pass. None of that belongs on the main thread, for the same reason the download
            // destination is not created there.
            val cached = withContext(Dispatchers.IO) {
                container.imagePreviews.cached(scope, entry.path, entry.sizeBytes, entry.modifiedAtMillis)
            }
            // Only when a transfer is actually coming. A picture that is already in the cache decodes in
            // milliseconds, and asking the server for a copy of it that cannot improve on that would be
            // a request, a read and a render spent on a frame nobody would have noticed.
            if (cached == null) {
                prefetchThumbnail(entry, generation)
            }
            val file = cached ?: downloadPreview(entry, scope, generation, authorize) ?: return@launch
            decodePreview(file, generation)
        }
    }

    /**
     * Asks the server for its own small copy of the picture, and does not wait for the answer.
     *
     * It runs *beside* the transfer rather than before it. Awaiting it would delay the progress bar by
     * a whole round trip, and the progress bar is the first thing the pane owes the user; started
     * alongside, this answers with a few kilobytes while the photograph itself is still arriving. That
     * is the whole trick: the *transport* becomes gradual, rather than only the decode.
     *
     * It never raises the elevation prompt, whatever [reloadPreview] was last asked to do. A prefetch
     * is the app's own idea rather than the user's, and asking for a password on nobody's behalf is
     * exactly what `RelaxKonOS.Mobile.V1.Design.md` §5.3.8 forbids. The consequence is deliberately
     * dull: a protected file answers `elevation-required` here and is simply left without a thumbnail,
     * and the download that follows is where authorization is asked for — from a card the user can
     * see, behind a button they pressed.
     *
     * Every other answer is equally uninteresting. `thumbnail-unsupported` is the ordinary case for a
     * file the host cannot draw, and a server without the route answers 404; both mean "no thumbnail",
     * which is what every image did before this existed, and neither is a state the user is told about.
     */
    private fun prefetchThumbnail(entry: RemoteEntry, generation: Int) {
        thumbnailJob?.cancel()
        thumbnailJob = viewModelScope.launch {
            val result = try {
                container.files.thumbnail(entry.path, SERVER_THUMBNAIL_EDGE_PX, ElevationAnswerProvider.Declines)
            } catch (cancelled: CancellationException) {
                // Never swallowed: this is the one exception that has to reach the coroutine machinery.
                throw cancelled
            } catch (_: Exception) {
                null
            }
            val bytes = when (result) {
                is ApiResult.Success -> result.value
                else -> return@launch
            }
            val bitmap = withContext(Dispatchers.IO) { container.imageDecoder.decode(bytes) } ?: return@launch
            if (generation != previewGeneration) {
                return@launch
            }
            thumbnail = bitmap
            // Only ever published into a transfer that is still running. A thumbnail that arrives once
            // the picture itself is on screen has nothing left to describe, and swapping a decoded
            // photograph for its own thumbnail would be a visible step backwards.
            preview = (preview as? ImagePreview.Downloading)?.copy(thumbnail = bitmap) ?: preview
        }
    }

    /**
     * Streams one image into the preview cache.
     *
     * The bytes are kept in `cacheDir` rather than offered as a download, because a preview is not
     * something the user asked to keep: it is what makes tapping the same picture twice cost one
     * transfer instead of two, and it is thrown away by the platform whenever storage runs short
     * (`ImagePreviewCache`).
     */
    private suspend fun downloadPreview(
        entry: RemoteEntry,
        scope: String,
        generation: Int,
        authorize: Boolean,
    ): File? {
        val target = withContext(Dispatchers.IO) {
            container.imagePreviews.create(scope, entry.path, entry.sizeBytes, entry.modifiedAtMillis)
        }
        if (generation == previewGeneration) {
            // A thumbnail that beat the first byte is carried in from here: it has to be on screen for
            // the whole of the transfer, not from the first progress callback onwards.
            preview = ImagePreview.Downloading(0, entry.sizeBytes, thumbnail = thumbnail)
        }
        val result = container.files.download(
            path = entry.path,
            target = target,
            provider = if (authorize) container.elevationAnswers else ElevationAnswerProvider.Declines,
        ) { written, total ->
            viewModelScope.launch(Dispatchers.Main.immediate) {
                if (generation == previewGeneration) {
                    // The announced length comes first and stays: a server that omits or mislabels it
                    // mid-stream must not turn a bar with a denominator into one without.
                    preview = ImagePreview.Downloading(written, total ?: entry.sizeBytes, thumbnail = thumbnail)
                }
            }
        }
        if (result is ApiResult.Success) {
            return target.file
        }
        if (generation == previewGeneration) {
            // A transfer that was refused, failed or interrupted leaves nothing usable, and a
            // truncated file left in the cache would be handed to the decoder the next time round.
            withContext(Dispatchers.IO) { runCatching { target.file.delete() } }
            val needsElevation = result is ApiResult.Problem && result.code == ProblemCodes.ELEVATION_REQUIRED
            preview = ImagePreview.Unavailable(
                message = if (needsElevation) {
                    UiMessage(R.string.files_preview_elevation)
                } else {
                    result.failureMessage() ?: UiMessage(R.string.files_preview_failed)
                },
                needsElevation = needsElevation,
            )
        }
        return null
    }

    /**
     * Decodes the cached file twice, small then large.
     *
     * This is the "thumbnail first" the preview is built around. The first pass is a 96-pixel square,
     * which costs almost nothing even for a forty-megapixel photograph, and publishing it is what puts
     * something recognisable on screen while the second pass — the one that can take a moment on a
     * large picture — is still running. Both passes read the same cached file, so the ladder is made of
     * decode time, not of a second transfer.
     *
     * [thumbnail] takes the place of that first pass whenever the server has already sent one. It is
     * the same rung of the ladder, arrived from the other side of the network, and re-decoding the file
     * down to 96 pixels would only produce a worse copy of a picture that is already in memory.
     */
    private suspend fun decodePreview(file: File, generation: Int) {
        val codec = container.imageDecoder
        val box = previewBounds
        val arrived = thumbnail
        val placeholder = withContext(Dispatchers.IO) {
            arrived ?: codec.decode(file, LOCAL_THUMBNAIL_EDGE_PX, LOCAL_THUMBNAIL_EDGE_PX)
        }
        if (generation != previewGeneration) {
            return
        }
        if (placeholder == null) {
            preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_failed), needsElevation = false)
            return
        }
        preview = ImagePreview.Ready(placeholder, null)
        val image = withContext(Dispatchers.IO) { codec.decode(file, box.width, box.height) }
        if (generation != previewGeneration) {
            return
        }
        previewFile = file
        previewDecodedFor = box
        withContext(Dispatchers.IO) { container.imagePreviews.touch(file) }
        preview = if (image == null) {
            ImagePreview.Unavailable(UiMessage(R.string.files_preview_failed), needsElevation = false)
        } else {
            ImagePreview.Ready(placeholder, image)
        }
    }

    /**
     * Re-reads the cached file for a box that grew — the viewer opening, or a rotation.
     *
     * Nothing is transferred: this is the same ladder [decodePreview] climbs, taken one rung higher
     * because something on screen now asks for more.
     */
    private fun upgradePreview(file: File, box: IntSize) {
        val generation = previewGeneration
        previewUpgradeJob?.cancel()
        previewUpgradeJob = viewModelScope.launch {
            val image = withContext(Dispatchers.IO) { container.imageDecoder.decode(file, box.width, box.height) }
            if (image == null || generation != previewGeneration) {
                return@launch
            }
            previewDecodedFor = box
            (preview as? ImagePreview.Ready)?.let { preview = it.copy(image = image) }
        }
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
    val pickUpload = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
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

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        ScreenHeader(title = stringResource(R.string.nav_files))

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
                enabled = viewModel.transfer == null && !uploadRunning,
                modifier = Modifier.weight(1f),
            ) {
                DesktopIcon(icon = DesktopIcons.upload, size = 18.dp)
                Spacer(Modifier.width(Spacing.sm))
                Text(stringResource(R.string.files_action_upload))
            }
        }

        // Above the list, not below it: an unfinished upload is a thing the user came here to act on,
        // and a card that only appears once every entry has been scrolled past is a card nobody sees.
        FileResumableUploadsCard(viewModel)

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
fun FileMessageBanner(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    viewModel.message?.let { banner ->
        ErrorBanner(
            message = banner.text(),
            onRetry = { viewModel.refresh() },
            onDismiss = { viewModel.dismissMessage() },
            modifier = modifier.padding(horizontal = Spacing.lg, vertical = Spacing.sm),
            tone = banner.tone,
        )
    }
}

/**
 * The progress card for the transfer in flight, pinned below the files content.
 *
 * Like the banner, it belongs to the destination: a download started from the detail page is still
 * running when that page is popped, and a card that left with it would be the only sign the user ever
 * gets that anything happened. The card is collapsible and never blocks navigation
 * (`RelaxKonOS.Mobile.V1.Design.md` §3.4).
 */
@Composable
fun FileTransferCard(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
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
            )

            else -> null
        },
        actions = actions,
    )
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
