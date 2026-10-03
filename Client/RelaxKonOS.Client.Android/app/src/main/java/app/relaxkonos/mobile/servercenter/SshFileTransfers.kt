package app.relaxkonos.mobile.servercenter

import java.io.InputStream
import java.io.OutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

object SshFileTransferRules {
    const val MAX_ENTRIES = 1000
    const val MAX_DEPTH = 32
    const val MAX_BYTES = 2_147_483_648L
    fun safeName(value: String): Boolean = value.isNotBlank() && value !in setOf(".", "..") &&
        value.length <= 255 && value.none { it == '/' || it == '\\' || it.isISOControl() }
    fun child(parent: String, name: String): String { require(safeName(name)); return parent.trimEnd('/') + "/" + name }
    fun canPaste(source: String, destination: String): Boolean = source != destination &&
        !destination.startsWith(source.trimEnd('/') + "/")
}

data class SshFileTransferItem(val entry: SshFileEntry, val relativePath: String)
data class SshFileTransferPlan(val items: List<SshFileTransferItem>, val totalBytes: Long)

/** Snapshot first; never follow a symlink or run a remote shell command. Partial writes are not retried. */
class SshFileTransfers(private val transport: ServerCenterSshTransport) {
    suspend fun plan(roots: List<SshFileEntry>, allowLinks: Boolean = false): SshFileTransferPlan {
        require(roots.isNotEmpty() && roots.size <= SshFileTransferRules.MAX_ENTRIES)
        require(roots.map { it.name }.distinct().size == roots.size)
        val result = mutableListOf<SshFileTransferItem>(); var bytes = 0L
        suspend fun visit(snapshot: SshFileEntry, relative: String, depth: Int) {
            currentCoroutineContext().ensureActive()
            require(depth <= SshFileTransferRules.MAX_DEPTH && result.size < SshFileTransferRules.MAX_ENTRIES) { "transfer-limit" }
            require(SshFileTransferRules.safeName(snapshot.name)) { "invalid-name" }
            val actual = transport.fileInfo(snapshot.path) ?: error("source-changed")
            require(actual == snapshot) { "source-changed" }
            require(allowLinks || !actual.isSymbolicLink) { "symlink-transfer" }
            if (!actual.isDirectory && !actual.isSymbolicLink) {
                val size = actual.size ?: error("source-changed")
                require(size >= 0 && size <= SshFileTransferRules.MAX_BYTES - bytes) { "transfer-limit" }; bytes += size
            }
            result += SshFileTransferItem(actual, relative)
            if (actual.isDirectory && !actual.isSymbolicLink) transport.listDirectory(actual.path).forEach { child ->
                require(child.path == SshFileTransferRules.child(actual.path, child.name)) { "source-changed" }
                visit(child, "$relative/${child.name}", depth + 1)
            }
        }
        roots.forEach { visit(it, it.name, 0) }
        return SshFileTransferPlan(result, bytes)
    }
    suspend fun copy(roots: List<SshFileEntry>, directory: String, cut: Boolean, beforeWrite: () -> Unit) {
        val plan = plan(roots)
        roots.forEach { root ->
            val destination = SshFileTransferRules.child(directory, root.name)
            require(SshFileTransferRules.canPaste(root.path, destination)) { "paste-self" }
            require(transport.fileInfo(destination) == null) { "destination-exists" }
        }
        if (cut) {
            roots.forEach { root ->
                currentCoroutineContext().ensureActive()
                require(transport.fileInfo(root.path) == root) { "source-changed" }
                beforeWrite()
                transport.rename(root.path, SshFileTransferRules.child(directory, root.name))
            }
        } else plan.items.forEach { item ->
            currentCoroutineContext().ensureActive()
            require(transport.fileInfo(item.entry.path) == item.entry) { "source-changed" }
            val destination = directory.trimEnd('/') + "/" + item.relativePath
            beforeWrite()
            if (item.entry.isDirectory) transport.createDirectory(destination) else transport.copyFile(item.entry.path, destination, item.entry.size!!)
        }
    }
    /** Exports folders/multiple selections as a streaming ZIP through SAF; no recursive local paths. */
    suspend fun exportZip(roots: List<SshFileEntry>, destination: OutputStream, progress: ((String, Long, Long) -> Unit)? = null) {
        val plan = plan(roots)
        var completed = 0L
        ZipOutputStream(destination).use { zip ->
            plan.items.forEach { item ->
                progress?.invoke(item.relativePath, completed, plan.totalBytes)
                currentCoroutineContext().ensureActive()
                require(transport.fileInfo(item.entry.path) == item.entry) { "source-changed" }
                zip.putNextEntry(ZipEntry(item.relativePath + if (item.entry.isDirectory) "/" else ""))
                if (!item.entry.isDirectory) {
                    val out = object : OutputStream() {
                        var count = 0L
                        override fun write(value: Int) { check(++count <= item.entry.size!!) { "source-changed" }; zip.write(value); progress?.invoke(item.relativePath, completed + count, plan.totalBytes) }
                        override fun write(buffer: ByteArray, offset: Int, length: Int) {
                            check(length.toLong() <= item.entry.size!! - count) { "source-changed" }; count += length; zip.write(buffer, offset, length)
                            progress?.invoke(item.relativePath, completed + count, plan.totalBytes)
                        }
                        override fun close() { /* Closing an SFTP stream must not close the ZIP. */ }
                    }
                    transport.download(item.entry.path, out)
                    check(out.count == item.entry.size) { "source-changed" }
                    completed += out.count
                }
                zip.closeEntry()
            }
        }
    }
}

/** Enforces actual bytes, even when a provider or remote file changes its reported size. */
class SshBoundedInputStream(private val source: InputStream, private val limit: Long) : InputStream() {
    var count = 0L; private set
    override fun read(): Int = source.read().also { if (it >= 0) { count++; check(count <= limit) { "transfer-limit" } } }
    override fun read(buffer: ByteArray, offset: Int, length: Int): Int = source.read(buffer, offset, length).also {
        if (it > 0) { count += it; check(count <= limit) { "transfer-limit" } }
    }
    override fun close() = source.close()
}

class SshBoundedOutputStream(private val destination: OutputStream, private val limit: Long,
    private val progress: ((Long) -> Unit)? = null) : OutputStream() {
    var count = 0L; private set
    override fun write(value: Int) { check(++count <= limit) { "transfer-limit" }; destination.write(value); progress?.invoke(count) }
    override fun write(buffer: ByteArray, offset: Int, length: Int) {
        check(length.toLong() <= limit - count) { "transfer-limit" }; count += length; destination.write(buffer, offset, length)
        progress?.invoke(count)
    }
    override fun close() { /* The caller owns the SAF/ZIP destination. */ }
}
