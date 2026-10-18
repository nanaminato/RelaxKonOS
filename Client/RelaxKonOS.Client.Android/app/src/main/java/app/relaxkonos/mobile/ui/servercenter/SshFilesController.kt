package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.data.FileListSnapshots
import android.graphics.BitmapFactory
import android.net.Uri
import android.app.Application
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.TransferProgress
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.OutputStream
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class SshClipboard(val hostId: String, val entries: List<SshFileEntry>, val cut: Boolean)
class SshPasteIntent(val clipboard: SshClipboard, val directory: String)
class SshAdoptIntent(val checks: List<SshFileCheck>)

class SshUploadIntent(val directory: String, val folder: Boolean)

class SshDownloadIntent(val entries: List<SshFileEntry>, val zip: Boolean, val directory: String)

class SshDetailIntent
class SshDiscardIntent(val detail: SshDetailIntent, val preview: SshPreview.Text)

class SshNameIntent

data class SshFileCheck(val path: String, val exists: Boolean?)

class SshFilesController(
    private val app: Application,
    private val workspaceHost: () -> String?,
    private val workspaceVersion: () -> Int,
    private val passwordCopy: (String) -> CharArray?,
    private val connect: suspend (String, CharArray) -> ServerCenterHostSession,
    private val pendingCleanup: MutableSet<ServerCenterHostSession>,
    private val startTransfer: (Int) -> Unit,
    private val scope: CoroutineScope,
) {
    private val documents = AndroidSshDocuments(app.contentResolver)
    private val cacheSettings = app.getSharedPreferences("file-list-cache", android.content.Context.MODE_PRIVATE)
    private val directoryCache = FileListSnapshots<List<SshFileEntry>>()
    private val mutable = MutableStateFlow(SshFilesUiState(cacheEnabled = cacheSettings.getBoolean("ssh-enabled", false)))
    val state = mutable.asStateFlow()
    @Volatile private var active = false
    @Volatile private var foreground = false
    @Volatile private var generation = 0
    private class Request(val hostId: String, val targets: List<String>?) { @Volatile var sent = false }
    private var activeRequest: Request? = null
    private val writeRequests = mutableSetOf<Request>()
    private var job: Job? = null; private var connection: ServerCenterHostSession? = null
    private var workspaceRevision = -1
    private val uncertain = mutableMapOf<String, Set<String>>()
    val transferLease: Int get() = generation
    val transferring: Boolean get() = state.value.busy && state.value.transfer != null

    fun resume(hostId: String) {
        val revision = workspaceVersion()
        if (state.value.hostId != hostId || workspaceRevision != revision) { stop(); directoryCache.clear(); mutable.value = SshFilesUiState(cacheEnabled = cacheSettings.getBoolean("ssh-enabled", false), hostId = hostId, unknown = hostId in uncertain, writesSettling = writeRequests.any { it.hostId == hostId && it.sent }); workspaceRevision = revision }
        active = true; foreground = true
        mutable.update { if (it.nameIntent == null && (it.newDirectory != null || it.renameTarget != null)) it.copy(nameIntent = SshNameIntent()) else it }
        mutable.update { if (it.detailIntent == null && it.detailEntry != null) it.copy(detailIntent = SshDetailIntent()) else it }
        if (!state.value.busy) reload(force = false)
    }
    fun stop() { activeRequest?.takeIf { it.sent && it.targets != null }?.let(::markUnknown); active = false; foreground = false; generation++; job?.cancel(); connection?.let { releaseSession(it) }; connection = null; pendingCleanup.toList().forEach { releaseSession(it) }; mutable.update { it.copy(busy = false, connected = false, transfer = null, deleteTarget = null, deleteEntries = emptyList(), nameIntent = null, detailIntent = null, discardIntent = null, pasteIntent = null, adoptIntent = null, checked = emptyList()) } }
    fun cancelTransfer() { val wasForeground = foreground; stop(); active = true; foreground = wasForeground }
    fun detach() { foreground = false; clearDelete(); mutable.update { it.copy(nameIntent = null, detailIntent = null, discardIntent = null, pasteIntent = null, adoptIntent = null, checked = emptyList()) }; if (!transferring) stop() }
    fun close() { try { stop() } finally { scope.cancel() } }
    fun keeperStopped(lease: Int) { if (lease == generation && transferring) cancelTransfer() }
    fun toggleFileListCache() {
        val enabled = !state.value.cacheEnabled
        cacheSettings.edit().putBoolean("ssh-enabled", enabled).apply()
        val refresh = state.value.showingCachedFiles
        directoryCache.clear(); mutable.update { it.copy(cacheEnabled = enabled, showingCachedFiles = false) }
        if (refresh) reload()
    }
    fun reload(force: Boolean = true) {
        if (force) directoryCache.clear()
        val path = state.value.path
        val cached = if (state.value.cacheEnabled && !force) directoryCache.get(path) else null
        if (cached != null) mutable.update { it.copy(entries = cached, showingCachedFiles = true) }
        execute(listingRead = true) { transport, _ -> val entries = transport.listDirectory(path); { old -> old.copy(entries = entries, showingCachedFiles = false, connected = true, selectedPaths = emptySet(), checked = emptyList()) } }
    }
    fun open(entry: SshFileEntry) {
        if (!currentWorkspace() || state.value.busy) return
        if ((state.value.preview as? SshPreview.Text)?.changed == true) return
        if (state.value.selecting) { toggle(entry); return }
        if (entry.isDirectory && !entry.isSymbolicLink) navigate(entry.path)
        else {
            mutable.update { it.copy(selected = entry, detailEntry = entry, preview = SshPreview.None, detailIntent = SshDetailIntent(), discardIntent = null) }
            if (entry.isSymbolicLink) return
            if (entry.isText) preview(entry, false) else if (entry.isImage) preview(entry, true)
        }
    }
    fun up() { if (state.value.path != "/") navigate(parent(state.value.path)) }
    fun navigate(path: String, historyIndex: Int? = null) {
        if (!currentWorkspace() || state.value.busy || state.value.newDirectory != null || state.value.renameTarget != null) return
        if ((state.value.preview as? SshPreview.Text)?.changed == true) return
        val normalized = path.trim().trimEnd('/').ifEmpty { "/" }
        if (!normalized.startsWith('/') || normalized.length > 4096 || normalized.any(Char::isISOControl) ||
            normalized.split('/').any { it in setOf(".", "..") }) { problem("invalid-name"); return }
        mutable.update { old ->
            val history = if (historyIndex == null) (old.history.take(old.historyIndex + 1) + normalized).takeLast(64) else old.history
            old.copy(uploadIntent = null, downloadIntent = null, downloadLaunchPending = false, path = normalized, entries = emptyList(), connected = false, detailEntry = null, selected = null, preview = SshPreview.None,
                selectedPaths = emptySet(), history = history, historyIndex = historyIndex ?: history.lastIndex, pasteIntent = null, adoptIntent = null, detailIntent = null, discardIntent = null)
        }; reload(force = false)
    }
    fun backDirectory() { val s = state.value; if (s.historyIndex > 0) navigate(s.history[s.historyIndex - 1], s.historyIndex - 1) }
    fun forwardDirectory() { val s = state.value; if (s.historyIndex < s.history.lastIndex) navigate(s.history[s.historyIndex + 1], s.historyIndex + 1) }
    fun search(value: String) { if (!state.value.busy) mutable.update { it.copy(search = value.take(256), selectedPaths = emptySet()) } }
    fun sort(value: SshFileSort) { if (!state.value.busy) mutable.update { it.copy(sort = value) } }
    fun reverseSort() { if (!state.value.busy) mutable.update { it.copy(descending = !it.descending) } }
    fun selectAllVisible() { if (!state.value.busy) mutable.update { it.copy(selecting = true, selectedPaths = it.visibleEntries.map(SshFileEntry::path).toSet()) } }

    private fun hasUnsavedText() = (state.value.preview as? SshPreview.Text)?.changed == true
    private fun detailReady(expected: SshDetailIntent) = currentWorkspace() && state.value.detailIntent === expected && !state.value.busy &&
        state.value.nameIntent == null && state.value.deleteEntries.isEmpty() && state.value.pasteIntent == null && state.value.adoptIntent == null
    fun closeDetail(expected: SshDetailIntent) {
        if (!detailReady(expected) || state.value.discardIntent != null) return
        val preview = state.value.preview as? SshPreview.Text
        if (preview?.changed == true) mutable.update { it.copy(discardIntent = SshDiscardIntent(expected, preview)) }
        else clearDetail()
    }
    fun cancelDiscard(expected: SshDiscardIntent) {
        if (detailReady(expected.detail) && state.value.discardIntent === expected) mutable.update { it.copy(discardIntent = null) }
    }
    fun discardDetail(expected: SshDiscardIntent) {
        if (detailReady(expected.detail) && state.value.discardIntent === expected && state.value.preview === expected.preview) clearDetail()
    }
    private fun clearDetail() = mutable.update { it.copy(detailEntry = null, selected = null, preview = SshPreview.None, detailIntent = null, discardIntent = null) }
    fun selectMode() { if (!state.value.busy) mutable.update { it.copy(selecting = !it.selecting, selectedPaths = emptySet()) } }
    fun toggle(entry: SshFileEntry) { if (!state.value.busy) mutable.update { it.copy(selectedPaths = if (entry.path in it.selectedPaths) it.selectedPaths - entry.path else it.selectedPaths + entry.path) } }
    private fun selected() = state.value.entries.filter { it.path in state.value.selectedPaths }
    fun clipboard(entry: SshFileEntry? = null, cut: Boolean) {
        if (!currentWorkspace() || state.value.busy || state.value.unknown) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isEmpty()) return
        mutable.update { it.copy(clipboard = SshClipboard(it.hostId, entries, cut), selecting = false, selectedPaths = emptySet(), pasteIntent = null) }
    }
    fun clearClipboard() { if (!state.value.busy) mutable.update { it.copy(clipboard = null, pasteIntent = null) } }
    fun requestPaste() {
        val snapshot = state.value
        val clipboard = snapshot.clipboard ?: return
        if (!currentWorkspace() || snapshot.busy || snapshot.unknown || hasUnsavedText() || snapshot.pasteIntent != null || clipboard.hostId != snapshot.hostId || clipboard.entries.isEmpty()) return
        if (clipboard.entries.any { !SshFileTransferRules.safeName(it.name) }) { problem("invalid-name"); return }
        mutable.update { it.copy(pasteIntent = SshPasteIntent(clipboard, snapshot.path)) }
    }
    fun cancelPaste(expected: SshPasteIntent) {
        if (state.value.pasteIntent === expected) mutable.update { it.copy(pasteIntent = null) }
    }
    fun paste(expected: SshPasteIntent) {
        val snapshot = state.value
        val clipboard = expected.clipboard
        if (!currentWorkspace() || snapshot.pasteIntent !== expected || snapshot.clipboard !== clipboard || snapshot.path != expected.directory ||
            snapshot.busy || snapshot.unknown || cleanupBlocked() || clipboard.hostId != snapshot.hostId) return
        mutable.update { it.copy(pasteIntent = null) }
        val targets = clipboard.entries.flatMap { listOf(it.path, SshFileTransferRules.child(snapshot.path, it.name)) }
        execute(targets, transfer = TransferProgress(clipboard.entries.first().name)) { transport, dispatch ->
            SshFileTransfers(transport).copy(clipboard.entries, snapshot.path, clipboard.cut, dispatch)
            val entries = transport.listDirectory(snapshot.path)
            return@execute { old -> old.copy(entries = entries, clipboard = if (clipboard.cut) null else old.clipboard, detailEntry = null, selected = null, preview = SshPreview.None, detailIntent = null, discardIntent = null) }
        }
    }
    private fun currentWorkspace() = active && foreground && workspaceHost() == state.value.hostId && workspaceVersion() == workspaceRevision
    private fun nameReady(expected: SshNameIntent, writing: Boolean = false) = currentWorkspace() &&
        state.value.nameIntent === expected && !state.value.busy && (!writing || !state.value.unknown && !hasUnsavedText() && !cleanupBlocked())
    fun beginCreateDirectory() {
        if (state.value.newDirectory != null || state.value.renameTarget != null || hasUnsavedText()) return
        if (currentWorkspace() && !state.value.busy && !state.value.unknown) mutable.update {
            it.copy(newDirectory = "", renameTarget = null, renameName = "", nameIntent = SshNameIntent(), problem = null)
        }
    }
    fun cancelCreateDirectory(expected: SshNameIntent) {
        if (nameReady(expected)) mutable.update { it.copy(newDirectory = null, nameIntent = null, problem = null) }
    }
    fun setNewDirectory(value: String, expected: SshNameIntent) {
        if (nameReady(expected) && !state.value.unknown) mutable.update { it.copy(newDirectory = value) }
    }
    fun createDirectory(expected: SshNameIntent) {
        if (!nameReady(expected, writing = true)) return
        val snapshot = state.value
        val name = snapshot.newDirectory?.trim().orEmpty()
        if (!SshFileTransferRules.safeName(name)) { problem("invalid-name"); return }
        val path = SshFileTransferRules.child(snapshot.path, name)
        execute(listOf(path)) { transport, dispatch ->
            check(transport.fileInfo(path) == null) { "destination-exists" }
            dispatch(); transport.createDirectory(path)
            val result = refreshed(transport, snapshot.path)
            return@execute { old -> result(old).copy(newDirectory = null, nameIntent = null) }
        }
    }
    fun askDelete(entry: SshFileEntry? = null) {
        if (!currentWorkspace() || state.value.busy || state.value.unknown || hasUnsavedText()) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isNotEmpty()) mutable.update { it.copy(deleteTarget = entries.first(), deleteEntries = entries) }
    }
    fun dismissDelete(expected: List<SshFileEntry>) {
        if (currentWorkspace() && !state.value.busy && state.value.deleteEntries === expected) clearDelete()
    }
    private fun clearDelete() = mutable.update { it.copy(deleteTarget = null, deleteEntries = emptyList()) }
    fun delete(expected: List<SshFileEntry>) {
        val snapshot = state.value; val roots = snapshot.deleteEntries
        if (roots !== expected || !currentWorkspace() || snapshot.busy || snapshot.unknown || hasUnsavedText() || cleanupBlocked()) return
        if (roots.isEmpty()) return
        clearDelete()
        execute(roots.map(SshFileEntry::path)) { transport, dispatch ->
            val plan = SshFileTransfers(transport).plan(roots, allowLinks = true)
            plan.items.asReversed().forEach { item ->
                val actual = transport.fileInfo(item.entry.path) ?: error("source-changed")
                check(if (item.entry.isDirectory && !item.entry.isSymbolicLink) actual.isDirectory && !actual.isSymbolicLink else actual == item.entry) { "source-changed" }
                dispatch(); transport.delete(item.entry.path, false)
            }
            refreshed(transport, snapshot.path)
        }
    }
    fun beginRename(entry: SshFileEntry) {
        if (state.value.newDirectory != null || state.value.renameTarget != null || hasUnsavedText()) return
        if (currentWorkspace() && !state.value.busy && !state.value.unknown) mutable.update {
            it.copy(renameTarget = entry, renameName = entry.name, newDirectory = null, nameIntent = SshNameIntent(), problem = null)
        }
    }
    fun cancelRename(expected: SshNameIntent) {
        if (nameReady(expected)) mutable.update { it.copy(renameTarget = null, renameName = "", nameIntent = null, problem = null) }
    }
    fun setRename(value: String, expected: SshNameIntent) {
        if (nameReady(expected) && !state.value.unknown) mutable.update { it.copy(renameName = value) }
    }
    fun rename(expected: SshNameIntent) {
        if (!nameReady(expected, writing = true)) return
        val snapshot = state.value
        val entry = snapshot.renameTarget ?: return; val name = snapshot.renameName.trim()
        if (!SshFileTransferRules.safeName(name)) { problem("invalid-name"); return }
        val destination = SshFileTransferRules.child(parent(entry.path), name)
        execute(listOf(entry.path, destination)) { transport, dispatch ->
            check(transport.fileInfo(entry.path) == entry) { "source-changed" }; check(transport.fileInfo(destination) == null) { "destination-exists" }
            dispatch(); transport.rename(entry.path, destination)
            val result = refreshed(transport, snapshot.path)
            return@execute { old -> result(old).copy(renameTarget = null, renameName = "", nameIntent = null) }
        }
    }
    fun requestUpload(folder: Boolean): SshUploadIntent? {
        if (!currentWorkspace() || state.value.busy || state.value.unknown || hasUnsavedText() ||
            state.value.nameIntent != null || state.value.deleteEntries.isNotEmpty() ||
            state.value.pasteIntent != null || state.value.adoptIntent != null) return null
        return SshUploadIntent(state.value.path, folder).also { expected -> mutable.update { it.copy(uploadIntent = expected) } }
    }
    fun upload(expected: SshUploadIntent, uris: List<Uri> = emptyList(), tree: Uri? = null) {
        val snapshot = state.value
        if (!currentWorkspace() || snapshot.busy || snapshot.unknown || snapshot.uploadIntent !== expected ||
            snapshot.path != expected.directory || hasUnsavedText() || snapshot.nameIntent != null ||
            snapshot.deleteEntries.isNotEmpty() || snapshot.pasteIntent != null || snapshot.adoptIntent != null) return
        if (expected.folder && uris.isNotEmpty() || !expected.folder && tree != null) return
        mutable.update { it.copy(uploadIntent = null) }
        if (uris.isEmpty() && tree == null) return
        val directory = expected.directory
        val targets = mutableListOf<String>()
        execute(targets, transfer = TransferProgress(""), uploading = true) { transport, dispatch ->
            val items = if (tree == null) documents.files(uris) else documents.tree(tree)
            val epoch = generation
            val totalBytes = if (items.any { !it.isDirectory && it.size == null }) null else items.filterNot { it.isDirectory }.sumOf { it.size ?: 0L }
            val roots = items.filter { '/' !in it.relativePath }
            roots.forEach { targets += directory.trimEnd('/') + "/" + it.relativePath }
            roots.forEach { check(transport.fileInfo(directory.trimEnd('/') + "/" + it.relativePath) == null) { "destination-exists" } }
            var total = 0L
            items.forEach { item ->
                val destination = directory.trimEnd('/') + "/" + item.relativePath
                if (item.isDirectory) { dispatch(); transport.createDirectory(destination) }
                else withContext(Dispatchers.IO) {
                    transferProgress(epoch, item.relativePath, total, totalBytes)
                    val input = app.contentResolver.openInputStream(item.uri) ?: error("source-changed")
                    SshBoundedInputStream(input, SshFileTransferRules.MAX_BYTES - total).use { bounded ->
                        dispatch(); transport.uploadNew(bounded, item.size, destination) { sent ->
                            transferProgress(epoch, item.relativePath, total + sent, totalBytes)
                        }; total += bounded.count
                    }
                }
            }
            refreshed(transport, directory)
        }
    }
    fun requestDownload(entry: SshFileEntry? = null) {
        if (!currentWorkspace() || state.value.busy) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isEmpty() || entries.any { it.isSymbolicLink }) { problem("symlink-transfer"); return }
        mutable.update { it.copy(downloadIntent = SshDownloadIntent(entries.toList(), entries.size > 1 || entries.first().isDirectory, it.path), downloadLaunchPending = true) }
    }
    fun downloadLaunchHandled(expected: SshDownloadIntent) {
        if (state.value.downloadIntent === expected) mutable.update { it.copy(downloadLaunchPending = false) }
    }
    fun downloadTo(destination: Uri?, expected: SshDownloadIntent) {
        val snapshot = state.value
        if (!currentWorkspace() || snapshot.busy || snapshot.downloadIntent !== expected || snapshot.path != expected.directory) return
        val entries = expected.entries
        mutable.update { it.copy(downloadIntent = null, downloadLaunchPending = false) }
        if (destination == null || entries.isEmpty()) return
        execute(transfer = TransferProgress(entries.first().name)) { transport, _ ->
            val plan = SshFileTransfers(transport).plan(entries)
            val epoch = generation
            transferProgress(epoch, entries.first().name, 0, plan.totalBytes)
            withContext(Dispatchers.IO) {
                val output = app.contentResolver.openOutputStream(destination) ?: error("source-changed")
                output.use { if (expected.zip) SshFileTransfers(transport).exportZip(entries, it) { name, bytes, total ->
                        transferProgress(epoch, name, bytes, total)
                    }
                    else {
                        val bounded = SshBoundedOutputStream(it, plan.totalBytes) { bytes ->
                            transferProgress(epoch, entries.single().name, bytes, plan.totalBytes)
                        }
                        transport.download(entries.single().path, bounded)
                        check(bounded.count == plan.totalBytes) { "source-changed" }
                    } }
            }
            return@execute { old -> old.copy(selecting = false, selectedPaths = emptySet()) }
        }
    }
    fun updateText(value: String, expected: SshDetailIntent) {
        if (!detailReady(expected) || state.value.unknown || state.value.discardIntent != null || value.toByteArray(Charsets.UTF_8).size > TEXT_LIMIT) return
        val preview = state.value.preview as? SshPreview.Text ?: return
        mutable.update { it.copy(preview = preview.copy(content = value, changed = true)) }
    }
    fun saveText(expected: SshDetailIntent) {
        if (!detailReady(expected) || state.value.unknown || state.value.discardIntent != null) return
        val preview = state.value.preview as? SshPreview.Text ?: return
        if (!preview.changed) return
        val bytes = preview.content.toByteArray(Charsets.UTF_8)
        if (bytes.size > TEXT_LIMIT) { problem("text-too-large"); return }
        execute(listOf(preview.entry.path)) { transport, dispatch ->
            check(transport.fileInfo(preview.entry.path) == preview.entry) { "source-changed" }; dispatch()
            transport.upload(ByteArrayInputStream(bytes), bytes.size.toLong(), preview.entry.path, null)
            val entry = transport.fileInfo(preview.entry.path) ?: error("source-changed")
            return@execute { old -> old.copy(preview = preview.copy(entry = entry, changed = false), detailEntry = entry, selected = entry) }
        }
    }
    fun checkUnknown() {
        val paths = uncertain[state.value.hostId]?.toList() ?: return
        execute { transport, _ ->
            val checks = paths.map { path -> try { SshFileCheck(path, transport.fileInfo(path) != null) } catch (cancelled: CancellationException) { throw cancelled } catch (_: Exception) { SshFileCheck(path, null) } }
            return@execute { old -> old.copy(checked = checks) }
        }
    }
    fun requestAdopt() {
        val snapshot = state.value
        if (!currentWorkspace() || !snapshot.unknown || snapshot.busy || snapshot.writesSettling || snapshot.adoptIntent != null ||
            snapshot.checked.isEmpty() || snapshot.checked.any { it.exists == null }) return
        mutable.update { it.copy(adoptIntent = SshAdoptIntent(snapshot.checked)) }
    }
    fun cancelAdopt(expected: SshAdoptIntent) {
        if (state.value.adoptIntent === expected) mutable.update { it.copy(adoptIntent = null) }
    }
    fun adoptFacts(expected: SshAdoptIntent) {
        val snapshot = state.value
        if (!currentWorkspace() || snapshot.adoptIntent !== expected || snapshot.checked !== expected.checks || !snapshot.unknown ||
            snapshot.busy || cleanupBlocked() || writeRequests.any { it.hostId == snapshot.hostId && it.sent } ||
            snapshot.checked.isEmpty() || snapshot.checked.any { it.exists == null }) return
        uncertain.remove(snapshot.hostId)
        mutable.update { it.copy(unknown = false, checked = emptyList(), clipboard = null, pasteIntent = null, adoptIntent = null) }
    }
    private fun preview(entry: SshFileEntry, image: Boolean) = execute { transport, _ ->
        val limit = if (image) IMAGE_LIMIT else TEXT_LIMIT
        check(entry.size != null && entry.size <= limit) { if (image) "image-unreadable" else "text-too-large" }
        check(transport.fileInfo(entry.path) == entry) { "source-changed" }
        val buffer = ByteArrayOutputStream(); transport.download(entry.path, boundedOutput(buffer, limit)); val bytes = buffer.toByteArray()
        check(bytes.size.toLong() == entry.size && transport.fileInfo(entry.path) == entry) { "source-changed" }
        val result = if (!image) {
            val text = runCatching { Charsets.UTF_8.newDecoder()
                .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                .decode(java.nio.ByteBuffer.wrap(bytes)).toString() }.getOrElse { error("text-unreadable") }
            SshPreview.Text(entry, text)
        } else {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }; BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            check(bounds.outWidth > 0 && bounds.outHeight > 0 && bounds.outWidth.toLong() * bounds.outHeight <= 4_194_304) { "image-unreadable" }
            val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: error("image-unreadable"); SshPreview.Image(entry, bitmap)
        }
        return@execute { old -> old.copy(preview = result) }
    }
    private suspend fun refreshed(transport: ServerCenterSshTransport, path: String): (SshFilesUiState) -> SshFilesUiState {
        val entries = transport.listDirectory(path)
        return { old -> old.copy(entries = entries, detailEntry = null, selected = null, preview = SshPreview.None, selectedPaths = emptySet(), detailIntent = null, discardIntent = null) }
    }
    private fun transferProgress(epoch: Int, name: String, bytes: Long, total: Long?) {
        mutable.update { if (epoch == generation && active && it.busy) it.copy(transfer = TransferProgress(name, bytes, total)) else it }
    }

    private fun execute(writeTargets: List<String>? = null, listingRead: Boolean = false, transfer: TransferProgress? = null, uploading: Boolean = false,
        action: suspend (ServerCenterSshTransport, () -> Unit) -> (SshFilesUiState) -> SshFilesUiState) {
        val snapshot = state.value
        if (!active || !foreground || snapshot.busy || writeTargets != null && snapshot.unknown || workspaceHost() != snapshot.hostId) return
        if (workspaceVersion() != workspaceRevision) return
        pendingCleanup.toList().forEach { releaseSession(it) }
        if (pendingCleanup.isNotEmpty()) { problem("cleanup-failed"); return }
        val secret = passwordCopy(snapshot.hostId) ?: run { problem("connection-failed"); return }
        if (writeTargets != null) directoryCache.clear()
        val epoch = ++generation; val workspaceEpoch = workspaceRevision; val request = Request(snapshot.hostId, writeTargets); activeRequest = request; if (writeTargets != null) writeRequests += request; var completed = false
        fun verify() { if (!active || epoch != generation || workspaceVersion() != workspaceEpoch || state.value.hostId != snapshot.hostId || workspaceHost() != snapshot.hostId) throw CancellationException("SFTP workspace changed") }
        val backgroundListing = listingRead && snapshot.showingCachedFiles
        mutable.update { it.copy(busy = !backgroundListing, problem = null, checked = emptyList(), pasteIntent = null, adoptIntent = null, transfer = transfer, uploading = uploading) }
        job = scope.launch {
            var session: ServerCenterHostSession? = null
            try {
                verify()
                if (transfer != null) startTransfer(epoch)
                session = connect(snapshot.hostId, secret)
                verify(); connection = session
                val result = action(session.sshTransport) { verify(); request.sent = true }
                verify(); mutable.update { result(it).copy(busy = false, problem = null, showingCachedFiles = false) }
                if (state.value.cacheEnabled && listingRead) directoryCache.put(state.value.path, state.value.entries.toList(), state.value.entries.size)
                completed = true
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { if (epoch == generation) problem(error.message?.takeIf { it in SAFE_PROBLEMS } ?: "connection-failed") }
            finally {
                if (request.sent && !completed && writeTargets != null) markUnknown(request)
                if (activeRequest === request) activeRequest = null
                session?.let { releaseSession(it, report = epoch == generation) }; if (connection === session) connection = null
                writeRequests.remove(request)
                if (state.value.hostId == snapshot.hostId) mutable.update { it.copy(writesSettling = writeRequests.any { pending -> pending.hostId == snapshot.hostId && pending.sent }) }
                secret.fill('\u0000')
                if (epoch == generation) mutable.update { it.copy(busy = false, transfer = null) }
            }
        }.also { it.invokeOnCompletion {
            secret.fill('\u0000')
            if (activeRequest === request) activeRequest = null
            writeRequests.remove(request)
            if (state.value.hostId == snapshot.hostId) mutable.update { state -> state.copy(
                writesSettling = writeRequests.any { pending -> pending.hostId == snapshot.hostId && pending.sent }) }
            if (epoch == generation) mutable.update { state -> state.copy(busy = false, transfer = null) }
        } }
    }
    private fun cleanupBlocked(): Boolean {
        if (pendingCleanup.isEmpty()) return false
        problem("cleanup-failed")
        return true
    }
    private fun releaseSession(session: ServerCenterHostSession, report: Boolean = true) {
        try { session.close() } catch (_: Exception) { }
        try { if (session.sshTransport.isConnected) session.sshTransport.close() }
        catch (_: Exception) { }
        val connected = runCatching { session.sshTransport.isConnected }.getOrDefault(true)
        if (connected) pendingCleanup += session else pendingCleanup -= session
        if (connected && report) problem("cleanup-failed")
    }
    private fun markUnknown(request: Request) {
        directoryCache.clear()
        uncertain[request.hostId] = (uncertain[request.hostId].orEmpty() + request.targets.orEmpty()).take(1000).toSet()
        if (state.value.hostId == request.hostId) mutable.update { it.copy(unknown = true, checked = emptyList(), writesSettling = writeRequests.any { pending -> pending.hostId == request.hostId && pending.sent }) }
    }
    private fun problem(value: String) = mutable.update { it.copy(problem = value) }
    private fun parent(path: String) = path.trimEnd('/').substringBeforeLast('/', "").ifBlank { "/" }
    private fun boundedOutput(destination: OutputStream, limit: Long) = SshBoundedOutputStream(destination, limit)
    private companion object {
        const val TEXT_LIMIT = 1_048_576L; const val IMAGE_LIMIT = 16_777_216L
        val SAFE_PROBLEMS = setOf("text-unreadable", "text-too-large", "image-unreadable", "invalid-name", "transfer-limit", "source-changed", "symlink-transfer", "paste-self", "destination-exists")
    }
}
