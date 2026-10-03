package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
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
data class SshFileCheck(val path: String, val exists: Boolean?)

class SshFilesViewModel(application: Application) : AndroidViewModel(application) {
    private val app = getApplication<RelaxKonApplication>()
    private val container = app.container
    private val documents = AndroidSshDocuments(app.contentResolver)
    private val mutable = MutableStateFlow(SshFilesUiState())
    val state = mutable.asStateFlow()
    @Volatile private var active = false
    @Volatile private var generation = 0
    private class Request(val hostId: String, val targets: List<String>?) { @Volatile var sent = false }
    private var activeRequest: Request? = null
    private val writeRequests = mutableSetOf<Request>()
    private var job: Job? = null; private var connection: ServerCenterHostSession? = null
    private var workspaceRevision = -1
    private val uncertain = mutableMapOf<String, Set<String>>()

    fun resume(hostId: String) {
        val revision = container.serverCenter.workspaceRevision
        if (state.value.hostId != hostId || workspaceRevision != revision) { stop(); mutable.value = SshFilesUiState(hostId = hostId, unknown = hostId in uncertain, writesSettling = writeRequests.any { it.hostId == hostId && it.sent }); workspaceRevision = revision }
        active = true; reload()
    }
    fun stop() { activeRequest?.takeIf { it.sent && it.targets != null }?.let(::markUnknown); active = false; generation++; job?.cancel(); connection?.close(); connection = null; mutable.update { it.copy(busy = false, connected = false, transfer = null) } }
    fun cancelTransfer() { stop(); active = true }
    override fun onCleared() { stop(); super.onCleared() }
    fun reload() {
        val path = state.value.path
        execute { transport, _ -> val entries = transport.listDirectory(path); { old -> old.copy(entries = entries, connected = true, selectedPaths = emptySet(), checked = emptyList()) } }
    }
    fun open(entry: SshFileEntry) {
        if (state.value.busy) return
        if (state.value.selecting) { toggle(entry); return }
        if (entry.isDirectory && !entry.isSymbolicLink) navigate(entry.path)
        else {
            mutable.update { it.copy(selected = entry, detailEntry = entry, preview = SshPreview.None) }
            if (entry.isSymbolicLink) return
            if (entry.isText) preview(entry, false) else if (entry.isImage) preview(entry, true)
        }
    }
    fun up() { if (state.value.path != "/") navigate(parent(state.value.path)) }
    fun navigate(path: String, historyIndex: Int? = null) {
        if (state.value.busy) return
        val normalized = path.trim().trimEnd('/').ifEmpty { "/" }
        if (!normalized.startsWith('/') || normalized.length > 4096 || normalized.any(Char::isISOControl) ||
            normalized.split('/').any { it in setOf(".", "..") }) { problem("invalid-name"); return }
        mutable.update { old ->
            val history = if (historyIndex == null) (old.history.take(old.historyIndex + 1) + normalized).takeLast(64) else old.history
            old.copy(path = normalized, entries = emptyList(), connected = false, detailEntry = null, selected = null, preview = SshPreview.None,
                selectedPaths = emptySet(), history = history, historyIndex = historyIndex ?: history.lastIndex)
        }; reload()
    }
    fun backDirectory() { val s = state.value; if (s.historyIndex > 0) navigate(s.history[s.historyIndex - 1], s.historyIndex - 1) }
    fun forwardDirectory() { val s = state.value; if (s.historyIndex < s.history.lastIndex) navigate(s.history[s.historyIndex + 1], s.historyIndex + 1) }
    fun search(value: String) { if (!state.value.busy) mutable.update { it.copy(search = value.take(256), selectedPaths = emptySet()) } }
    fun sort(value: SshFileSort) { if (!state.value.busy) mutable.update { it.copy(sort = value) } }
    fun reverseSort() { if (!state.value.busy) mutable.update { it.copy(descending = !it.descending) } }
    fun selectAllVisible() { if (!state.value.busy) mutable.update { it.copy(selecting = true, selectedPaths = it.visibleEntries.map(SshFileEntry::path).toSet()) } }

    fun closeDetail() {
        if (state.value.busy) return
        if ((state.value.preview as? SshPreview.Text)?.changed == true) mutable.update { it.copy(discardRequested = true) }
        else discardDetail()
    }
    fun cancelDiscard() = mutable.update { it.copy(discardRequested = false) }
    fun discardDetail() = mutable.update { it.copy(detailEntry = null, selected = null, preview = SshPreview.None, discardRequested = false) }
    fun selectMode() { if (!state.value.busy) mutable.update { it.copy(selecting = !it.selecting, selectedPaths = emptySet()) } }
    fun toggle(entry: SshFileEntry) { if (!state.value.busy) mutable.update { it.copy(selectedPaths = if (entry.path in it.selectedPaths) it.selectedPaths - entry.path else it.selectedPaths + entry.path) } }
    private fun selected() = state.value.entries.filter { it.path in state.value.selectedPaths }
    fun clipboard(entry: SshFileEntry? = null, cut: Boolean) {
        if (state.value.busy || state.value.unknown) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isEmpty()) return
        mutable.update { it.copy(clipboard = SshClipboard(it.hostId, entries, cut), selecting = false, selectedPaths = emptySet()) }
    }
    fun clearClipboard() { if (!state.value.busy) mutable.update { it.copy(clipboard = null) } }
    fun paste() {
        val snapshot = state.value; val clipboard = snapshot.clipboard ?: return
        if (clipboard.hostId != snapshot.hostId) return
        val targets = clipboard.entries.flatMap { listOf(it.path, SshFileTransferRules.child(snapshot.path, it.name)) }
        execute(targets) { transport, dispatch ->
            SshFileTransfers(transport).copy(clipboard.entries, snapshot.path, clipboard.cut, dispatch)
            val entries = transport.listDirectory(snapshot.path)
            return@execute { old -> old.copy(entries = entries, clipboard = if (clipboard.cut) null else old.clipboard, detailEntry = null, selected = null, preview = SshPreview.None) }
        }
    }
    fun beginCreateDirectory() { if (!state.value.busy && !state.value.unknown) mutable.update { it.copy(newDirectory = "") } }
    fun cancelCreateDirectory() = mutable.update { it.copy(newDirectory = null) }
    fun setNewDirectory(value: String) = mutable.update { it.copy(newDirectory = value) }
    fun createDirectory() {
        val snapshot = state.value; val name = snapshot.newDirectory?.trim().orEmpty()
        if (!SshFileTransferRules.safeName(name)) { problem("invalid-name"); return }
        val path = SshFileTransferRules.child(snapshot.path, name); cancelCreateDirectory()
        execute(listOf(path)) { transport, dispatch -> check(transport.fileInfo(path) == null) { "destination-exists" }; dispatch(); transport.createDirectory(path); refreshed(transport, snapshot.path) }
    }
    fun askDelete(entry: SshFileEntry? = null) {
        if (state.value.busy || state.value.unknown) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isNotEmpty()) mutable.update { it.copy(deleteTarget = entries.first(), deleteEntries = entries) }
    }
    fun dismissDelete() = mutable.update { it.copy(deleteTarget = null, deleteEntries = emptyList()) }
    fun delete() {
        val snapshot = state.value; val roots = snapshot.deleteEntries
        if (roots.isEmpty()) return
        dismissDelete()
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
    fun beginRename(entry: SshFileEntry) { if (!state.value.busy && !state.value.unknown) mutable.update { it.copy(renameTarget = entry, renameName = entry.name) } }
    fun cancelRename() = mutable.update { it.copy(renameTarget = null, renameName = "") }
    fun setRename(value: String) = mutable.update { it.copy(renameName = value) }
    fun rename() {
        val snapshot = state.value; val entry = snapshot.renameTarget ?: return; val name = snapshot.renameName.trim()
        if (!SshFileTransferRules.safeName(name)) { problem("invalid-name"); return }
        val destination = SshFileTransferRules.child(parent(entry.path), name); cancelRename()
        execute(listOf(entry.path, destination)) { transport, dispatch ->
            check(transport.fileInfo(entry.path) == entry) { "source-changed" }; check(transport.fileInfo(destination) == null) { "destination-exists" }
            dispatch(); transport.rename(entry.path, destination); refreshed(transport, snapshot.path)
        }
    }
    fun upload(uris: List<Uri>, tree: Uri? = null) {
        val directory = state.value.path
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
        if (state.value.busy) return
        val entries = entry?.let(::listOf) ?: selected()
        if (entries.isEmpty() || entries.any { it.isSymbolicLink }) { problem("symlink-transfer"); return }
        mutable.update { it.copy(downloadEntries = entries, downloadTarget = entries.first(), downloadZip = entries.size > 1 || entries.first().isDirectory, downloadLaunchPending = true) }
    }
    fun downloadLaunchHandled() = mutable.update { it.copy(downloadLaunchPending = false) }
    fun downloadTo(destination: Uri?) {
        val snapshot = state.value; val entries = snapshot.downloadEntries
        mutable.update { it.copy(downloadTarget = null, downloadEntries = emptyList(), downloadLaunchPending = false) }
        if (destination == null || entries.isEmpty()) return
        execute(transfer = TransferProgress(entries.first().name)) { transport, _ ->
            val plan = SshFileTransfers(transport).plan(entries)
            val epoch = generation
            transferProgress(epoch, entries.first().name, 0, plan.totalBytes)
            withContext(Dispatchers.IO) {
                val output = app.contentResolver.openOutputStream(destination) ?: error("source-changed")
                output.use { if (snapshot.downloadZip) SshFileTransfers(transport).exportZip(entries, it) { name, bytes, total ->
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
    fun updateText(value: String) {
        if (state.value.busy || value.toByteArray(Charsets.UTF_8).size > TEXT_LIMIT) return
        val preview = state.value.preview as? SshPreview.Text ?: return
        mutable.update { it.copy(preview = preview.copy(content = value, changed = true)) }
    }
    fun saveText() {
        val preview = state.value.preview as? SshPreview.Text ?: return
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
    fun adoptFacts() {
        val snapshot = state.value
        if (snapshot.busy || writeRequests.any { it.hostId == snapshot.hostId && it.sent } || snapshot.checked.isEmpty() || snapshot.checked.any { it.exists == null }) return
        uncertain.remove(snapshot.hostId); mutable.update { it.copy(unknown = false, checked = emptyList(), clipboard = null) }
    }
    private fun preview(entry: SshFileEntry, image: Boolean) = execute { transport, _ ->
        val limit = if (image) IMAGE_LIMIT else TEXT_LIMIT
        check(entry.size != null && entry.size <= limit) { if (image) "image-unreadable" else "text-too-large" }
        val buffer = ByteArrayOutputStream(); transport.download(entry.path, boundedOutput(buffer, limit)); val bytes = buffer.toByteArray()
        val result = if (!image) SshPreview.Text(entry, bytes.toString(Charsets.UTF_8)) else {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }; BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            check(bounds.outWidth > 0 && bounds.outHeight > 0 && bounds.outWidth.toLong() * bounds.outHeight <= 4_194_304) { "image-unreadable" }
            val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: error("image-unreadable"); SshPreview.Image(entry, bitmap)
        }
        return@execute { old -> old.copy(preview = result) }
    }
    private suspend fun refreshed(transport: ServerCenterSshTransport, path: String): (SshFilesUiState) -> SshFilesUiState {
        val entries = transport.listDirectory(path)
        return { old -> old.copy(entries = entries, detailEntry = null, selected = null, preview = SshPreview.None, selectedPaths = emptySet()) }
    }
    private fun transferProgress(epoch: Int, name: String, bytes: Long, total: Long?) {
        mutable.update { if (epoch == generation && active && it.busy) it.copy(transfer = TransferProgress(name, bytes, total)) else it }
    }

    private fun execute(writeTargets: List<String>? = null, transfer: TransferProgress? = null, uploading: Boolean = false,
        action: suspend (ServerCenterSshTransport, () -> Unit) -> (SshFilesUiState) -> SshFilesUiState) {
        val snapshot = state.value
        if (!active || snapshot.busy || writeTargets != null && snapshot.unknown || container.serverCenter.sshFilesHostId != snapshot.hostId) return
        val secret = container.serverCenter.verifiedPasswordCopy(snapshot.hostId) ?: run { problem("connection-failed"); return }
        val epoch = ++generation; val workspaceEpoch = workspaceRevision; val request = Request(snapshot.hostId, writeTargets); activeRequest = request; if (writeTargets != null) writeRequests += request; var completed = false
        fun verify() { if (!active || epoch != generation || container.serverCenter.workspaceRevision != workspaceEpoch || state.value.hostId != snapshot.hostId || container.serverCenter.sshFilesHostId != snapshot.hostId) throw CancellationException("SFTP workspace changed") }
        mutable.update { it.copy(busy = true, problem = null, checked = emptyList(), transfer = transfer, uploading = uploading) }
        job = viewModelScope.launch {
            var session: ServerCenterHostSession? = null
            try {
                session = container.serverCenterConnections.connect(snapshot.hostId, SshCredential(SshCredentialKind.Password, secret, null), System.currentTimeMillis())
                verify(); connection = session
                val result = action(session.sshTransport) { verify(); request.sent = true }
                verify(); mutable.update { result(it).copy(busy = false, problem = null) }; completed = true
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { if (epoch == generation) problem(error.message?.takeIf { it in SAFE_PROBLEMS } ?: "connection-failed") }
            finally {
                if (request.sent && !completed && writeTargets != null) markUnknown(request)
                if (activeRequest === request) activeRequest = null
                session?.close(); if (connection === session) connection = null
                writeRequests.remove(request)
                if (state.value.hostId == snapshot.hostId) mutable.update { it.copy(writesSettling = writeRequests.any { pending -> pending.hostId == snapshot.hostId && pending.sent }) }
                secret.fill('\u0000')
                if (epoch == generation) mutable.update { it.copy(busy = false, transfer = null) }
            }
        }.also { it.invokeOnCompletion { secret.fill('\u0000') } }
    }
    private fun markUnknown(request: Request) {
        uncertain[request.hostId] = (uncertain[request.hostId].orEmpty() + request.targets.orEmpty()).take(1000).toSet()
        if (state.value.hostId == request.hostId) mutable.update { it.copy(unknown = true, checked = emptyList(), writesSettling = writeRequests.any { pending -> pending.hostId == request.hostId && pending.sent }) }
    }
    private fun problem(value: String) = mutable.update { it.copy(problem = value) }
    private fun parent(path: String) = path.trimEnd('/').substringBeforeLast('/', "").ifBlank { "/" }
    private fun boundedOutput(destination: OutputStream, limit: Long) = SshBoundedOutputStream(destination, limit)
    private companion object {
        const val TEXT_LIMIT = 1_048_576L; const val IMAGE_LIMIT = 16_777_216L
        val SAFE_PROBLEMS = setOf("text-too-large", "image-unreadable", "invalid-name", "transfer-limit", "source-changed", "symlink-transfer", "paste-self", "destination-exists")
    }
}
