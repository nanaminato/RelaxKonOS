package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.UploadProtocol
import java.io.File
import java.io.FileInputStream
import java.io.InputStream
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Raised when a document cannot be re-read from a non-zero offset.
 *
 * Not an error condition to report: the picker handing back a cloud-backed document answers with a
 * pipe, and a pipe cannot be positioned. It is the signal that this document has to be staged into the
 * app's private cache before it can travel resumably.
 */
class SeekUnsupportedException(message: String) : Exception(message)

/** Raised when the private cache cannot hold the document. Reported as a named, actionable failure. */
class InsufficientCacheSpaceException(val requiredBytes: Long, val availableBytes: Long) :
    Exception("cache requires $requiredBytes bytes, $availableBytes available")

/**
 * Bytes that can be read again from any offset.
 *
 * This is the whole technical prerequisite of resumption: every rule in the protocol assumes the client
 * can re-read `[offset, offset + chunk)` after a lost response or a network change. An `InputStream`
 * from a picker cannot do that, which is why it is never what an upload reads from.
 */
interface UploadSource {
    /** Declared total length. A resumable session cannot be opened without one. */
    val length: Long

    val displayName: String

    /**
     * Opens a stream positioned at [offset]. The caller closes it.
     *
     * Throws [SeekUnsupportedException] when the source cannot start there, rather than silently
     * returning a stream at zero — which would corrupt the destination file.
     */
    fun openAt(offset: Long): InputStream
}

/** A file this device owns: a staged cache copy, or a document that already lives on local storage. */
class FileUploadSource(private val file: File, override val displayName: String) : UploadSource {
    override val length: Long get() = file.length()

    override fun openAt(offset: Long): InputStream =
        FileInputStream(file).also { it.channel.position(offset) }

    /** Where the bytes are, for a diagnostic that has to name the file it read. */
    val path: String get() = file.absolutePath
}

/**
 * The picker's answer for one document, before anything has been decided about how it will travel.
 *
 * [length] and [lastModifiedMillis] are nullable on purpose: a provider is allowed to refuse both, and
 * "the provider did not say" is a different fact from "the file is empty". A document that cannot be
 * positioned, or whose length is unknown, is copied into the cache before it is uploaded.
 */
class PickedDocument(
    /** The URI this document was opened from, kept so a resume can reopen it after a process restart. */
    val uri: String,
    val displayName: String,
    val length: Long?,
    val lastModifiedMillis: Long?,
    val open: () -> InputStream,
    val openAt: (Long) -> InputStream,
) {
    /**
     * Whether the provider can position at an arbitrary offset.
     *
     * The probe opens one byte past the start and closes again: a real file answers instantly, while a
     * pipe-backed cloud document throws on the first seek. Nothing is read, so a large document costs
     * nothing to test.
     */
    fun supportsSeek(): Boolean = runCatching { openAt(if ((length ?: 0L) > 0L) 1L else 0L).close() }.isSuccess

    /**
     * The signature a resume entry and a cache file are keyed on.
     *
     * A document whose length or modification time changed is not the document the session started
     * with: resuming into it would publish a mixture of the old and the new file, which is worse than
     * starting again.
     */
    val sourceKey: String get() = "$uri|${length ?: -1L}|${lastModifiedMillis ?: -1L}"

    /** Adapts this document to an [UploadSource] without copying it anywhere, or `null` when its length is unknown. */
    fun asSource(): UploadSource? = length?.takeIf { it >= 0L }?.let { known ->
        object : UploadSource {
            override val length: Long get() = known
            override val displayName: String get() = this@PickedDocument.displayName
            override fun openAt(offset: Long): InputStream = this@PickedDocument.openAt(offset)
        }
    }
}

/** A source ready to upload, together with the cache copy it may be reading from. */
class StagedUpload(val source: UploadSource, val cacheFile: File?)

/**
 * Re-opens a document by URI.
 *
 * A resume entry stores the URI, never a stream: after a process restart the only way back to the
 * bytes is to ask the provider again. It returns `null` when the document is no longer readable —
 * the picker's grant does not outlive the process, and a file on a removed SD card is gone — which the
 * coordinator reports as a source that must be picked again rather than as a transfer failure.
 */
fun interface UploadDocumentOpener {
    fun open(uriString: String): PickedDocument?
}

/**
 * Turns a picked document into something a resumable upload can read, copying it into the app's
 * private cache when the provider cannot seek or will not report a length.
 *
 * The cache lives in `cacheDir`, which the platform may empty and which is never backed up. Its files
 * are not user data — they are a second copy of a document that belongs to whoever picked it — so they
 * are deleted the moment they stop being useful.
 */
class UploadSourceStager(
    private val cacheRoot: File,
    /** Free space, injectable so a check can make the guard fire without filling a disk. */
    private val freeBytes: (File) -> Long = { it.usableSpace },
) {
    /** The cache file [document] uses. Stable for a given source, so a resume finds it again. */
    fun cacheFileFor(document: PickedDocument): File = cacheFileFor(document.sourceKey)

    /** The cache file one source signature owns. This is how a deleted entry deletes its payload. */
    fun cacheFileFor(sourceKey: String): File = File(cacheRoot, sha1Hex(sourceKey))

    /**
     * Resolves [document] into a readable source.
     *
     * [onPreparing] is invoked when the bytes have to be copied first. Staging is a stage of its own and
     * must never be counted as upload progress: claiming data has reached the server while it has not
     * yet left the device is the same lie as a progress bar that fills before the socket does.
     */
    suspend fun stage(document: PickedDocument, onPreparing: () -> Unit = {}): StagedUpload {
        val length = document.length
        if (length != null && length >= 0L && document.supportsSeek()) {
            // The guard cannot be null here, but returning the guaranteed-to-exist value keeps the
            // contract explicit instead of relying on a `!!` that a later edit could make wrong.
            document.asSource()?.let { return StagedUpload(it, null) }
        }

        onPreparing()
        val target = cacheFileFor(document)
        if (length != null && length >= 0L) {
            val required = (length * CACHE_HEADROOM).toLong()
            val available = freeBytes(cacheRoot)
            if (available < required) throw InsufficientCacheSpaceException(required, available)
        }
        return withContext(Dispatchers.IO) {
            copyIntoCache(document, target)
            StagedUpload(FileUploadSource(target, document.displayName), target)
        }
    }

    /**
     * Deletes the cache copy of one source. Called exactly when its resume entry is removed, so a cache
     * file can never outlive the entry that explains it.
     */
    fun discardCache(sourceKey: String) {
        cacheFileFor(sourceKey).delete()
    }

    /**
     * Deletes every cache file no live resume entry owns, and reports how many went.
     *
     * Without this an abandoned session's copy of a 3 GB video would sit in the cache until the
     * platform decided to reclaim space — the pairing of entry and cache has to be enforced, not
     * assumed.
     */
    fun trim(ownedSourceKeys: Set<String>): Int {
        val owned = ownedSourceKeys.mapTo(HashSet()) { cacheFileFor(it).name }
        val files = cacheRoot.listFiles() ?: return 0
        var removed = 0
        for (file in files) {
            if (!file.isFile || file.name in owned) continue
            if (file.delete()) removed++
        }
        return removed
    }

    private fun copyIntoCache(document: PickedDocument, target: File) {
        target.parentFile?.mkdirs()
        var written = 0L
        try {
            document.open().use { input ->
                target.outputStream().buffered(COPY_BUFFER_SIZE).use { output ->
                    val buffer = ByteArray(COPY_BUFFER_SIZE)
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        output.write(buffer, 0, count)
                        written += count
                        // Checked as we go rather than once up front, because a provider that would not
                        // report a length is exactly the case where no up-front check is possible.
                        if (written % FREE_SPACE_CHECK_INTERVAL == 0L) {
                            val available = freeBytes(cacheRoot)
                            if (available < COPY_BUFFER_SIZE.toLong() * 4) {
                                throw InsufficientCacheSpaceException(written + COPY_BUFFER_SIZE.toLong() * 4, available)
                            }
                        }
                    }
                }
            }
        } catch (error: Throwable) {
            // A half-written copy is not a document: leaving it behind would make the next attempt read
            // a truncated file that happens to have the right name.
            target.delete()
            throw error
        }
    }

    companion object {
        /** Headroom over the document's own length, so a copy cannot leave the device wedged. */
        const val CACHE_HEADROOM = 1.1

        /** One 80 KiB buffer, matching every other transfer path in the app. */
        const val COPY_BUFFER_SIZE = 81_920

        /** How often the running free-space check runs during a copy of unknown length. */
        private const val FREE_SPACE_CHECK_INTERVAL = 4L * 1024 * 1024

        /** The default cache directory, under the platform's own cache root. */
        fun defaultCacheRoot(cacheDir: File): File = File(cacheDir, "uploads")

        internal fun sha1Hex(value: String): String {
            val digest = MessageDigest.getInstance("SHA-1").digest(value.toByteArray(Charsets.UTF_8))
            return digest.joinToString("") { "%02x".format(it) }
        }
    }
}

/** True when [length] is small enough for the single-request route. */
fun isSingleShotLength(length: Long): Boolean = length <= UploadProtocol.SINGLE_SHOT_THRESHOLD_BYTES
