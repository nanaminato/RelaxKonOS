package app.relaxkonos.mobile.ui.files


import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.UploadProblemCodes
import app.relaxkonos.mobile.data.FileBrowserPolicy
import app.relaxkonos.mobile.data.FileSort
import app.relaxkonos.mobile.data.FileBatchAction
import app.relaxkonos.mobile.data.FileBatchReport
import app.relaxkonos.mobile.data.FileBatchRunner
import app.relaxkonos.mobile.data.DownloadTarget
import app.relaxkonos.mobile.data.Transfer
import app.relaxkonos.mobile.data.TransferKind
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.data.PickedDocument
import app.relaxkonos.mobile.data.RecentOperationKind
import app.relaxkonos.mobile.data.UploadResumeEntry
import app.relaxkonos.mobile.data.UploadState
import app.relaxkonos.mobile.data.isDecodableImage
import app.relaxkonos.mobile.data.isSingleShotLength
import app.relaxkonos.mobile.service.UploadForegroundService
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.fileReadFailureMessage
import app.relaxkonos.mobile.ui.common.withDebugDetail
import java.io.File
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
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

    var query by mutableStateOf("")
    var showHidden by mutableStateOf(false)
    var sort by mutableStateOf(FileSort.Name)
    var sortDescending by mutableStateOf(false)
    var selectionMode by mutableStateOf(false)
        private set
    var checkedPaths by mutableStateOf<Set<String>>(emptySet())
        private set
    val visibleEntries get() = FileBrowserPolicy.visible(listing?.entries.orEmpty(), query, showHidden, sort, sortDescending)
    private val backPaths = mutableListOf<String>()
    private val forwardPaths = mutableListOf<String>()
    var canGoBack by mutableStateOf(false)
        private set
    var canGoForward by mutableStateOf(false)
        private set
    var mutationBusy by mutableStateOf(false)
        private set
    var batchTarget by mutableStateOf<FileBatchTarget?>(null)
        private set
    var batchRunning by mutableStateOf(false)
        private set
    var batchStopRequested by mutableStateOf(false)
        private set
    var batchProgress by mutableStateOf(0)
        private set
    var batchTotal by mutableStateOf(0)
        private set
    var batchPath by mutableStateOf("")
        private set
    var batchReport by mutableStateOf<FileBatchReport?>(null)
        private set
    var clipboard by mutableStateOf<FileBatchTarget?>(null)
        private set
    var permissionsOpen by mutableStateOf(false)
        private set
    var permissionRecursive by mutableStateOf(false)
    var permissionInput by mutableStateOf("")
    var permissionMessage by mutableStateOf<UiMessage?>(null)
        private set
    var propertyMessage by mutableStateOf<UiMessage?>(null)
        private set
    var propertiesNeedsElevation by mutableStateOf(false)
        private set
    var viewerTransform by mutableStateOf(ViewerTransform())
        private set
    private var mutationJob: Job? = null
    private var propertiesJob: Job? = null
    private var propertyRequest = 0L
    val canMutate get() = !loading && !directoryReadFailed && listing != null && !mutationBusy && !batchRunning && transfer == null && batchReport == null &&
        container.activeSession?.executionEligibility?.available == true && !container.uploads.isRunning && !container.fileTransfers.isRunning
    val checkedEntries get() = listing?.entries.orEmpty().filter { it.path in checkedPaths }

    fun toggleSelection() {
        if (batchRunning) return
        selectionMode = !selectionMode
        checkedPaths = emptySet()
    }
    fun check(entry: RemoteEntry) {
        if (entry.path !in checkedPaths && checkedPaths.size >= FileBrowserPolicy.MAX_BATCH) return
        if (!batchRunning && FileBrowserPolicy.mutable(entry)) checkedPaths = if (entry.path in checkedPaths)
            checkedPaths - entry.path else checkedPaths + entry.path
    }
    fun selectVisible() { if (!batchRunning) checkedPaths = visibleEntries.filter(FileBrowserPolicy::mutable)
        .take(FileBrowserPolicy.MAX_BATCH).map { it.path }.toSet() }
    fun copySelection(move: Boolean) {
        if (checkedEntries.isEmpty() || !canMutate) return
        clipboard = FileBatchTarget(FileBrowserPolicy.snapshot(checkedEntries), if (move) FileBatchAction.Move else FileBatchAction.Copy)
    }
    fun requestBatchDelete() {
        if (checkedEntries.isNotEmpty() && canMutate) batchTarget = FileBatchTarget(FileBrowserPolicy.snapshot(checkedEntries), FileBatchAction.Delete)
    }
    fun paste() { if (canMutate && path.isNotBlank()) batchTarget = clipboard }
    fun cancelBatchTarget() { batchTarget = null }
    fun stopBatch() { batchStopRequested = true }
    fun dismissBatchReport() { batchReport = null }
    fun clearClipboard() { if (!batchRunning) clipboard = null }
    fun confirmBatch(directory: String?) {
        val target = batchTarget ?: return
        if (!canMutate) return
        if (target.action != FileBatchAction.Delete &&
            !FileBrowserPolicy.validDestination(target.entries, directory.orEmpty())) {
            message = UiMessage(R.string.files_invalid_destination); return
        }
        batchTarget = null
        runBatch(target) { entry -> container.files.childOf(directory!!, entry.name) }
    }
    private fun runBatch(target: FileBatchTarget, destination: (RemoteEntry) -> String) {
        val owner = container.activeSession ?: return
        if (!canMutate) return
        batchRunning = true
        batchStopRequested = false
        batchProgress = 0
        batchTotal = target.entries.size
        mutationJob = viewModelScope.launch {
            try {
                val result = FileBatchRunner(container.files, container.session).run(owner, target.entries, target.action,
                    destination, container.elevationAnswers, { batchStopRequested }) { index, entry ->
                    batchProgress = index; batchPath = entry.path
                }
                if (container.activeSession !== owner) return@launch
                batchReport = result
                result.completed.forEach { entryPath -> container.recentOperations.record(when (target.action) {
                    FileBatchAction.Copy -> RecentOperationKind.Copy
                    FileBatchAction.Move -> RecentOperationKind.Move
                    FileBatchAction.Delete -> RecentOperationKind.Delete
                }, entryPath) }
                checkedPaths = checkedPaths - result.completed.toSet()
                if (target.action != FileBatchAction.Copy && selected?.path in result.completed) select(null)
                if (target === clipboard && target.action == FileBatchAction.Move) {
                    val remaining = target.entries.filterNot { it.path in result.completed }
                    clipboard = if (remaining.isEmpty()) null else target.copy(entries = remaining)
                }
                reload()
            } finally {
                if (container.activeSession === owner) { batchRunning = false; mutationJob = null }
            }
        }
    }
    fun openPermissions() {
        val mode = properties?.unixMode ?: return
        if (!canMutate) return
        permissionRecursive = false
        permissionInput = FileBrowserPolicy.formatMode(mode)
        permissionMessage = null
        permissionsOpen = true
    }
    fun closePermissions() { if (!mutationBusy) permissionsOpen = false }
    fun savePermissions() {
        val target = selected ?: return
        val mode = FileBrowserPolicy.parseMode(permissionInput) ?: return
        val owner = container.activeSession ?: return
        if (!canMutate) return
        mutationBusy = true
        permissionMessage = null
        mutationJob = viewModelScope.launch {
            try {
                val result = container.files.setPermissions(target.path, mode, permissionRecursive && target.isDirectory, container.elevationAnswers)
                if (container.activeSession !== owner) return@launch
                if (result is ApiResult.Success && result.value.path == target.path && result.value.unixMode == mode) {
                    if (selected?.path == target.path) properties = result.value
                    permissionsOpen = false
                    message = UiMessage(R.string.files_permissions_saved, tone = StatusTone.Success)
                } else {
                    val unknown = result is ApiResult.Transport || result is ApiResult.Success ||
                        (result is ApiResult.Problem && result.status >= 500)
                    if (unknown) {
                        message = UiMessage(R.string.files_mutation_unknown)
                        permissionsOpen = false
                        if (selected?.path == target.path) loadProperties(target.path)
                    } else {
                        permissionMessage = result.failureMessage()
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (container.activeSession === owner) {
                    permissionsOpen = false
                    message = UiMessage(R.string.files_mutation_unknown)
                    if (selected?.path == target.path) loadProperties(target.path)
                }
            } finally { if (container.activeSession === owner) { mutationBusy = false; mutationJob = null } }
        }
    }
    fun reloadProperties(authorize: Boolean = false) { selected?.let { loadProperties(it.path, authorize) } }
    fun changeViewerTransform(transform: ViewerTransform) { viewerTransform = transform }
    val viewerEntries get() = visibleEntries.filter { it.isDecodableImage() }
    fun switchViewer(delta: Int) {
        val entries = viewerEntries
        val index = entries.indexOfFirst { it.path == selected?.path }
        val next = entries.getOrNull(index + delta) ?: return
        select(next)
        viewerOpen = true
    }

    var editorOpen by mutableStateOf(false)
    var editorPath by mutableStateOf<String?>(null)

    fun editText(path: String? = null) { editorPath = path; editorOpen = true }

    var path by mutableStateOf("")
        private set

    var listing by mutableStateOf<DirectoryListing?>(null)
        private set

    var loading by mutableStateOf(false)
        private set
    var directoryReadFailed by mutableStateOf(false)
        private set

    var message by mutableStateOf<UiMessage?>(null)
        private set
    var nameOperationMessage by mutableStateOf<UiMessage?>(null)
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

    private var localTransfer by mutableStateOf<Transfer?>(null)
    private var streamedTransfer by mutableStateOf<Transfer?>(null)
    var transfer: Transfer?
        get() = streamedTransfer ?: localTransfer
        private set(value) { localTransfer = value }
    private var directoryJob: Job? = null
    private var directoryRequest = 0L

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
        viewModelScope.launch { container.fileTransfers.state.collect { streamedTransfer = it } }
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
    private var activeSession: SessionState.Active? = null

    init {
        // ViewModels outlive the authenticated shell because they are stored by the Activity. A
        // new login must therefore be a hard boundary for server-owned UI state: retaining a Linux
        // path such as /home/nanami/下载 after signing in to a Windows host would otherwise send
        // that path to the new server on refresh or on the next operation.
        viewModelScope.launch {
            container.session.state.collect { state ->
                if (state is SessionState.Active) {
                    if (activeSession !== state) {
                        activeSession = state
                        val wasStarted = started
                        resetForSessionBoundary()
                        if (wasStarted) start()
                    }
                } else if (activeSession != null) {
                    activeSession = null
                    resetForSessionBoundary()
                }
            }
        }
    }

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
        if (batchRunning || mutationBusy || nextPath == path) return
        backPaths.add(path); if (backPaths.size > 100) backPaths.removeAt(0)
        forwardPaths.clear(); updateHistory()
        navigate(nextPath)
    }
    fun goBack() {
        if (!canGoBack || batchRunning || mutationBusy) return
        forwardPaths.add(path); navigate(backPaths.removeAt(backPaths.lastIndex)); updateHistory()
    }
    fun goForward() {
        if (!canGoForward || batchRunning || mutationBusy) return
        backPaths.add(path); navigate(forwardPaths.removeAt(forwardPaths.lastIndex)); updateHistory()
    }
    private fun updateHistory() { canGoBack = backPaths.isNotEmpty(); canGoForward = forwardPaths.isNotEmpty() }
    private fun navigate(nextPath: String) {
        path = nextPath
        listing = null
        query = ""
        checkedPaths = emptySet()
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
        propertyRequest++
        propertiesJob?.cancel()
        propertiesLoading = false
        propertiesNeedsElevation = false
        permissionsOpen = false
        permissionMessage = null
        viewerTransform = ViewerTransform()
        selected = entry
        properties = null
        preview = ImagePreview.Hidden
        propertyMessage = null
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
        if (!canMutate || path.isBlank()) return
        newDirectoryOpen = true
        nameOperationMessage = null
    }

    fun cancelNewDirectory() {
        if (mutationBusy) return
        newDirectoryOpen = false
    }

    fun confirmNewDirectory(name: String) {
        if (!FileBrowserPolicy.validName(name) || !canMutate || path.isBlank()) return
        val owner = container.activeSession ?: return
        val target = container.files.childOf(path, name)
        mutationBusy = true
        nameOperationMessage = null
        mutationJob = viewModelScope.launch {
            try {
            val result = fileMutationRequest { container.files.createDirectory(target, container.elevationAnswers) }
            if (container.activeSession !== owner) return@launch
            when (result) {
                is ApiResult.Success -> {
                    newDirectoryOpen = false
                    message = UiMessage(R.string.files_created, listOf(target), tone = StatusTone.Success)
                    container.recentOperations.record(RecentOperationKind.CreateDirectory, target)
                    reload()
                }

                else -> if (result is ApiResult.Transport || (result is ApiResult.Problem && result.status >= 500)) {
                    newDirectoryOpen = false
                    message = mutationFailure(result)
                } else nameOperationMessage = mutationFailure(result)
            }
            } finally { if (container.activeSession === owner) { mutationBusy = false; mutationJob = null } }
        }
    }

    fun requestRename(entry: RemoteEntry) {
        if (canMutate && FileBrowserPolicy.mutable(entry)) {
            nameOperationMessage = null
            renameTarget = entry
        }
    }

    fun cancelRename() {
        if (mutationBusy) return
        renameTarget = null
    }

    fun confirmRename(newName: String) {
        val target = renameTarget ?: return
        val owner = container.activeSession ?: return
        if (!FileBrowserPolicy.validName(newName) || !canMutate) return
        mutationBusy = true
        nameOperationMessage = null
        mutationJob = viewModelScope.launch {
            try {
            val result = fileMutationRequest { container.files.rename(target.path, newName, container.elevationAnswers) }
            if (container.activeSession !== owner) return@launch
            when (result) {
                is ApiResult.Success -> {
                    renameTarget = null
                    container.recentOperations.record(RecentOperationKind.Rename, target.path)
                    val newPath = container.files.childOf(container.files.parentOf(target.path), newName)
                    checkedPaths = checkedPaths.map { if (it == target.path) newPath else it }.toSet()
                    clipboard = clipboard?.let { copy -> copy.copy(entries = copy.entries.map {
                        if (it.path == target.path) it.copy(path = newPath, name = newName) else it
                    }) }
                    if (selected?.path == target.path) select(target.copy(path = newPath, name = newName))
                    reload()
                }
                else -> if (result is ApiResult.Transport || (result is ApiResult.Problem && result.status >= 500)) {
                    renameTarget = null
                    message = mutationFailure(result)
                } else nameOperationMessage = mutationFailure(result)
            }
            } finally { if (container.activeSession === owner) { mutationBusy = false; mutationJob = null } }
        }
    }

    fun requestDelete(entry: RemoteEntry) {
        if (canMutate && FileBrowserPolicy.mutable(entry)) deleteTarget = entry
    }

    fun requestTransfer(entry: RemoteEntry, move: Boolean) {
        if (canMutate && FileBrowserPolicy.mutable(entry)) transferTarget = TransferTarget(entry, move)
    }

    fun cancelTransferTarget() {
        transferTarget = null
    }

    fun confirmTransfer(destinationPath: String) {
        val request = transferTarget ?: return
        if (!canMutate || !FileBrowserPolicy.validRemotePath(destinationPath) || destinationPath == request.entry.path ||
            (request.entry.isDirectory && !FileBrowserPolicy.validDestination(listOf(request.entry), destinationPath))) return
        transferTarget = null
        runBatch(FileBatchTarget(listOf(request.entry), if (request.move) FileBatchAction.Move else FileBatchAction.Copy)) { destinationPath }
    }

    fun cancelDelete() {
        deleteTarget = null
    }

    fun confirmDelete() {
        val target = deleteTarget ?: return
        if (!canMutate) return
        deleteTarget = null
        runBatch(FileBatchTarget(listOf(target), FileBatchAction.Delete)) { "" }
    }

    /**
     * Streams a remote file into this device's Downloads.
     *
     * The transfer is owned by the application coordinator. It must not need a screen to be
     * mounted to finish or to be reported: in the Compact and Medium layouts the file detail is a
     * pushed page, so the list is out of the composition while the download runs, and anything the
     * download needs — the progress card, the result message, the confirmation — has to come from
     * somewhere that outlives both routes (`FileTransferCard`, `FileActionFeedback`).
     */
    fun download(entry: RemoteEntry) {
        if (transfer != null || container.fileTransfers.isRunning || batchRunning || mutationBusy) {
            return
        }
        container.activeSession ?: return
        if (needsNotificationPermission()) uploadNotificationPrompt = true
        container.fileTransfers.start(Transfer(label = entry.name, kind = TransferKind.Download)) { generation ->
            var target: DownloadTarget? = null
            try {
                // Creating the destination is a write into someone else's storage (a `MediaStore`
                // insert, or a directory that has to be made). It happens off the main thread, and
                // after the card is already on screen, so the tap is acknowledged before the first
                // byte and without a stalled frame.
                val created = withContext(Dispatchers.IO) {
                    runCatching { container.downloads.create(entry.name) }.also { target = it.getOrNull() }
                }
                if (created.isFailure) {
                    container.showNotice(UiMessage(R.string.files_download_failed).withDebugDetail(created.exceptionOrNull()?.message))
                    return@start
                }
                val destination = created.getOrThrow()
                target = destination
                if (!container.fileTransfers.isCurrent(generation)) return@start
                val result = container.files.download(entry.path, destination, container.elevationAnswers) { written, total ->
                    container.fileTransfers.update(generation) { it.copy(
                            transferredBytes = written,
                            totalBytes = total,
                    ) }
                }
                if (!container.fileTransfers.isCurrent(generation)) return@start
                when (result) {
                    is ApiResult.Success -> {
                        // Pending until this call: the file becomes visible to the rest of the device
                        // only once it is complete. A commit the platform refuses means the bytes are
                        // not actually in Downloads, so it is reported as a failure rather than
                        // followed by a success message that would not be true.
                        val committed = withContext(NonCancellable + Dispatchers.IO) {
                            runCatching { destination.commit() }.also { if (it.isSuccess) target = null }
                        }
                        if (committed.isSuccess) {
                            if (!container.fileTransfers.isCurrent(generation)) return@start
                            container.recentOperations.record(RecentOperationKind.Download, entry.path)
                            container.showNotice(UiMessage(R.string.files_downloaded, listOf(destination.location), tone = StatusTone.Success))
                        } else {
                            container.showNotice(UiMessage(R.string.files_download_failed).withDebugDetail(committed.exceptionOrNull()?.message))
                        }
                    }
                    else -> result.failureMessage()?.let(container::showNotice)
                }
            } catch (_: CancellationException) {
                // Expected when the user cancels the transfer.
            } finally {
                // A refused, failed or cancelled transfer leaves no file: on the shared Downloads
                // collection an abandoned row would be a corrupt entry for the whole device to see.
                withContext(NonCancellable + Dispatchers.IO) { target?.let { runCatching { it.discard() } } }
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
        container.activeSession ?: return
        val directory = path
        if (directory.isBlank()) { message = UiMessage(R.string.files_upload_needs_folder); return }
        if (needsNotificationPermission()) uploadNotificationPrompt = true
        container.fileTransfers.start(Transfer(label = getApplication<Application>().getString(R.string.files_upload_preparing), kind = TransferKind.Upload)) { generation ->
            try {
                val document = withContext(Dispatchers.IO) { runCatching { container.uploadDocuments.open(uri.toString()) }.getOrNull() }
                if (!container.fileTransfers.isCurrent(generation)) return@start
                if (document == null) { container.showNotice(UiMessage(R.string.files_upload_unreadable)); return@start }
                if (document.length != null && isSingleShotLength(document.length!!)) uploadSingleShot(document, directory, generation)
                else startResumable(document, directory)
            } catch (_: CancellationException) {
                // The request is not replayed when the user cancels or switches owner.
            }
        }
    }

    /** True while either route is busy; only one transfer runs at a time. */
    private fun isUploadBusy(): Boolean = transfer != null || container.fileTransfers.isRunning || batchRunning || mutationBusy || container.uploads.isRunning

    /**
     * Hands the document to the resumable coordinator.
     *
     * Nothing here is awaited: the transfer is the coordinator's, so the card keeps rendering after this
     * ViewModel is gone and the transfer survives the page. The foreground service is what keeps Android
     * from freezing the process the moment the user leaves the app.
     */
    private fun startResumable(document: PickedDocument, directory: String) {
        transfer = null
        uploadCollapsed = false
        container.uploads.start(directory, document)
        // Started before the prompt, not after: a foreground service has to be up within seconds, and the
        // permission request must never be a precondition for the transfer the user asked for.
        try { UploadForegroundService.start(getApplication()) }
        catch (error: Exception) { container.uploads.interrupt(); throw error }
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
    private suspend fun uploadSingleShot(document: PickedDocument, directory: String, generation: Long) {
        val app = getApplication<RelaxKonApplication>()
        val name = document.displayName.ifBlank { app.getString(R.string.files_upload_default_name) }
        container.fileTransfers.update(generation) { it.copy(label = name, totalBytes = document.length) }
        val result = runCatching {
            withContext(Dispatchers.IO) { document.open() }.use { input ->
                container.files.upload(directory, name, input, document.length, container.elevationAnswers) { written ->
                    container.fileTransfers.update(generation) { it.copy(transferredBytes = written) }
                }
            }
        }.getOrElse {
            if (it is CancellationException) throw it
            ApiResult.Transport(it.message)
        }
        if (!container.fileTransfers.isCurrent(generation)) return
        when (result) {
            is ApiResult.Success -> {
                container.showNotice(UiMessage(R.string.files_uploaded, listOf(name), tone = StatusTone.Success))
                container.recentOperations.record(RecentOperationKind.Upload, name)
                reload()
            }

            is ApiResult.Problem if result.code == UploadProblemCodes.TOO_LARGE_FOR_SINGLE_SHOT ->
                startResumable(document, directory)

            else -> mutationFailure(result)?.let(container::showNotice)
        }
    }

    private fun mutationFailure(result: ApiResult<*>): UiMessage? =
        if (result is ApiResult.Transport || (result is ApiResult.Problem && result.status >= 500))
            UiMessage(R.string.files_mutation_unknown) else result.failureMessage()

    /** Continues an unfinished upload the coordinator remembers. */
    fun resumeUpload(entry: UploadResumeEntry) {
        if (isUploadBusy()) return
        uploadCollapsed = false
        container.uploads.resume(entry)
        try { UploadForegroundService.start(getApplication()) }
        catch (_: Exception) {
            container.uploads.interrupt()
            container.showNotice(UiMessage(R.string.files_mutation_unknown))
        }
        if (needsNotificationPermission()) uploadNotificationPrompt = true
    }

    /** Abandons an unfinished upload: session, resume entry and cache copy all go. */
    fun discardUpload(entry: UploadResumeEntry) = container.uploads.discard(entry)

    /** Stops the resumable upload in flight and abandons it. */
    fun cancelUpload() = container.uploads.cancel()

    /** Clears a stopped upload card that has no session left to continue. */
    fun dismissUpload() = container.uploads.dismiss()

    fun cancelActiveTransfer() {
        container.fileTransfers.cancel()
        transfer = null
    }

    fun setTransferCollapsed(collapsed: Boolean) {
        if (container.fileTransfers.isRunning) container.fileTransfers.update(container.fileTransfers.lease) { it.copy(collapsed = collapsed) }
        else transfer = transfer?.copy(collapsed = collapsed)
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
     * file operation follows (`Shell.Design.md` §5.3.8).
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
        val owner = container.activeSession ?: return
        val scope = "${owner.serviceId}\u0000${owner.userName}"
        previewJob = viewModelScope.launch {
            try {
            // Reading the cache is stat calls and, on a miss, a directory listing followed by an
            // eviction pass. None of that belongs on the main thread, for the same reason the download
            // destination is not created there.
            val cached = withContext(Dispatchers.IO) {
                container.imagePreviews.cached(scope, entry.path, entry.sizeBytes, entry.modifiedAtMillis)
            }
            // Only when a transfer is actually coming. A picture that is already in the cache decodes in
            // milliseconds, and asking the server for a copy of it that cannot improve on that would be
            // a request, a read and a render spent on a frame nobody would have noticed.
            if (generation != previewGeneration || container.activeSession !== owner) return@launch
            if (cached == null) {
                prefetchThumbnail(entry, generation)
            }
            val file = cached ?: downloadPreview(entry, scope, generation, authorize, owner) ?: return@launch
            decodePreview(file, generation)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                if (generation == previewGeneration) preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_failed), false)
            }
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
     * exactly what `Shell.Design.md` §5.3.8 forbids. The consequence is deliberately
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
        val owner = container.activeSession ?: return
        thumbnailJob = viewModelScope.launch {
            if (generation != previewGeneration || container.activeSession !== owner) return@launch
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
        owner: SessionState.Active,
    ): File? {
        if (entry.sizeBytes != null && entry.sizeBytes > app.relaxkonos.mobile.data.ImagePreviewCache.CACHE_BUDGET_BYTES) {
            if (generation == previewGeneration) preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_too_large), false)
            return null
        }
        val target = withContext(Dispatchers.IO) {
            container.imagePreviews.create(scope, entry.path, entry.sizeBytes, entry.modifiedAtMillis)
        }
        try {
            if (generation != previewGeneration || container.activeSession !== owner) return null
            if (generation == previewGeneration) preview = ImagePreview.Downloading(0, entry.sizeBytes, thumbnail)
            val result = container.files.download(entry.path, target,
                if (authorize) container.elevationAnswers else ElevationAnswerProvider.Declines) { written, total ->
                viewModelScope.launch(Dispatchers.Main.immediate) {
                    if (generation == previewGeneration) preview = ImagePreview.Downloading(written, total ?: entry.sizeBytes, thumbnail)
                }
            }
            if (generation != previewGeneration) return null
            if (result is ApiResult.Success) {
                val committed = withContext(Dispatchers.IO) { runCatching { target.commit(result.value) } }
                if (committed.isSuccess) return committed.getOrThrow()
            }
            val elevation = result is ApiResult.Problem && result.code == ProblemCodes.ELEVATION_REQUIRED
            if (generation == previewGeneration) preview = ImagePreview.Unavailable(
                if (elevation) UiMessage(R.string.files_preview_elevation) else UiMessage(R.string.files_preview_failed), elevation)
            return null
        } finally { withContext(NonCancellable + Dispatchers.IO) { target.discard() } }
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

    private fun loadProperties(targetPath: String, authorize: Boolean = false) {
        val owner = container.activeSession ?: return
        val request = ++propertyRequest
        propertiesJob?.cancel()
        propertiesLoading = true
        properties = null
        propertyMessage = null
        propertiesNeedsElevation = false
        propertiesJob = viewModelScope.launch {
            try {
                val result = container.files.properties(targetPath, if (authorize) container.elevationAnswers else ElevationAnswerProvider.Declines)
                if (request != propertyRequest || container.activeSession !== owner || selected?.path != targetPath) return@launch
                when (result) {
                    is ApiResult.Success -> if (result.value.path == targetPath) properties = result.value
                        else propertyMessage = UiMessage(R.string.files_detail_unavailable)
                    else -> {
                        propertiesNeedsElevation = result is ApiResult.Problem && result.code == ProblemCodes.ELEVATION_REQUIRED
                        if (!propertiesNeedsElevation) propertyMessage = result.fileReadFailureMessage()
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (request == propertyRequest && container.activeSession === owner && selected?.path == targetPath) {
                    propertyMessage = UiMessage(R.string.files_detail_unavailable)
                }
            } finally { if (request == propertyRequest && container.activeSession === owner) propertiesLoading = false }
        }
    }

    private fun reload() {
        val request = ++directoryRequest
        val requestedPath = path
        directoryJob?.cancel()
        loading = true
        directoryReadFailed = false
        directoryJob = viewModelScope.launch {
            try {
                when (val result = container.files.list(requestedPath, container.elevationAnswers)) {
                    is ApiResult.Success -> if (request == directoryRequest) {
                        listing = result.value
                        path = result.value.path
                        checkedPaths = checkedPaths.intersect(result.value.entries.map { it.path }.toSet())
                        selected?.let { old ->
                            val current = result.value.entries.firstOrNull { it.path == old.path }
                            if (current != old) select(current)
                        }
                    }

                    else -> if (request == directoryRequest) {
                        directoryReadFailed = true
                        message = result.fileReadFailureMessage()
                    }
                }
            } catch (cancelled: kotlinx.coroutines.CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (request == directoryRequest) {
                    directoryReadFailed = true
                    message = UiMessage(R.string.files_read_failed)
                }
            } finally {
                if (request == directoryRequest) {
                    loading = false
                    directoryJob = null
                }
            }
        }
    }

    /** Clears every screen-owned value that is meaningful only to one authenticated server session. */
    private fun resetForSessionBoundary() {
        mutationJob?.cancel(); mutationJob = null
        propertyRequest++; propertiesJob?.cancel(); propertiesJob = null
        checkedPaths = emptySet(); selectionMode = false; clipboard = null
        batchTarget = null; batchReport = null; batchRunning = false; batchStopRequested = false
        batchPath = ""; batchProgress = 0; batchTotal = 0; mutationBusy = false
        backPaths.clear(); forwardPaths.clear(); updateHistory()
        query = ""; showHidden = false; sort = FileSort.Name; sortDescending = false
        permissionsOpen = false; permissionInput = ""; propertiesNeedsElevation = false
        permissionMessage = null
        propertyMessage = null
        viewerTransform = ViewerTransform()
        editorOpen = false
        editorPath = null
        directoryRequest++
        directoryJob?.cancel()
        directoryJob = null
        cancelPreview()

        started = false
        path = ""
        listing = null
        loading = false
        directoryReadFailed = false
        message = null
        selected = null
        properties = null
        propertiesLoading = false
        newDirectoryOpen = false
        renameTarget = null
        nameOperationMessage = null
        deleteTarget = null
        transferTarget = null
        transfer = null
        uploadCollapsed = false
        uploadNotificationPrompt = false
        preview = ImagePreview.Hidden
        previewFile = null
        previewDecodedFor = IntSize.Zero
        thumbnail = null
        viewerOpen = false
    }

}

data class TransferTarget(val entry: RemoteEntry, val move: Boolean)
data class FileBatchTarget(val entries: List<RemoteEntry>, val action: FileBatchAction)
