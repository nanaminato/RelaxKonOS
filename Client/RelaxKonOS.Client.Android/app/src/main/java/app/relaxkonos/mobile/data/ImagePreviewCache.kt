package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.DownloadSink
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.security.MessageDigest

/** A private staging download. Only a confirmed, size-checked transfer becomes a cache hit. */
class PreviewTarget(val file: File, private val destination: File, private val expectedLength: Long?, private val release: () -> Unit) : DownloadSink {
    override fun open(): OutputStream = object : java.io.FilterOutputStream(FileOutputStream(file)) {
        private var written = 0L
        override fun write(value: Int) { checkLength(1); out.write(value) }
        override fun write(bytes: ByteArray, offset: Int, count: Int) { checkLength(count); out.write(bytes, offset, count) }
        private fun checkLength(count: Int) {
            if (written + count > ImagePreviewCache.CACHE_BUDGET_BYTES) throw java.io.IOException("Image preview exceeds cache budget.")
            written += count
        }
    }
    fun commit(received: Long): File {
        require(received > 0 && file.length() == received && (expectedLength == null || expectedLength == received))
        java.nio.file.Files.move(file.toPath(), destination.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING)
        release()
        return destination
    }
    fun discard() { file.delete(); release() }
}

/**
 * Cache of remote images the user has looked at.
 *
 * ## Identity, not names
 *
 * An entry is keyed by the server/account namespace, the remote path and the file's size and modification time, hashed
 * into a fixed-length name. Three things follow from that, and all three are the reason for it:
 *
 *  - A remote path is not a file name: it can contain `/`, `\`, `:` and anything else a Windows path
 *    is allowed to hold, none of which can appear in one.
 *  - A file that changed on the host produces a different key, so an edited image is re-fetched
 *    instead of being shown from a stale copy.
 *  - Servers and accounts have independent namespaces, including the same path on one host.
 *
 * ## Size
 *
 * The cache is bounded, and the bound is enforced by [trim] on the oldest entries. Eviction can
 * therefore delete a file whose bitmap is still on screen; that costs a later re-read of a file that
 * is already decoded in memory, which is to say nothing the user can see.
 */
class ImagePreviewCache(private val directory: File) {
    private val activeStaging = mutableSetOf<String>()
    /**
     * The cached copy of one remote file, or `null` when there is none to reuse.
     *
     * A file of the wrong length is not a hit: see [PreviewTarget] for why the length is the test.
     */
    fun cached(scope: String, remotePath: String, sizeBytes: Long?, modifiedMillis: Long?): File? {
        val file = fileNameFor(scope, remotePath, sizeBytes, modifiedMillis)
        if (!file.isFile || file.length() == 0L) {
            return null
        }
        if (sizeBytes != null && file.length() != sizeBytes) {
            return null
        }
        return file
    }

    /** Independent staging files prevent a cancelled old transfer from overwriting a newer preview. */
    @Synchronized fun create(scope: String, remotePath: String, sizeBytes: Long?, modifiedMillis: Long?): PreviewTarget {
        directory.mkdirs()
        trim()
        require(sizeBytes == null || sizeBytes <= CACHE_BUDGET_BYTES)
        val destination = fileNameFor(scope, remotePath, sizeBytes, modifiedMillis)
        val staging = File(directory, destination.name + "." + java.util.UUID.randomUUID() + ".part")
        activeStaging += staging.name
        return PreviewTarget(staging, destination, sizeBytes) { synchronized(this) { activeStaging -= staging.name; trim() } }
    }

    /** Records that [file] was used just now, so the least recently *viewed* image is evicted first. */
    fun touch(file: File) {
        file.setLastModified(System.currentTimeMillis())
    }

    /** Deletes the least recently used entries until the cache fits inside [budgetBytes]. */
    @Synchronized fun trim(budgetBytes: Long = CACHE_BUDGET_BYTES) {
        val files = directory.listFiles().orEmpty()
        val stagingName = Regex("preview-[0-9a-f]{40}\\.preview\\.[0-9a-f-]{36}\\.part")
        files.filter { stagingName.matches(it.name) && it.name !in activeStaging }.forEach { it.delete() }
        val entries = files.mapNotNull { file ->
            file.takeIf { it.isFile && it.name.endsWith(PREVIEW_SUFFIX) }
                ?.let { PreviewEntry(it.name, it.length(), it.lastModified()) }
        }
        val byName = files.associateBy { it.name }
        previewsToEvict(entries, budgetBytes).forEach { byName[it.name]?.delete() }
    }

    private fun fileNameFor(scope: String, remotePath: String, sizeBytes: Long?, modifiedMillis: Long?): File {
        val name = previewCacheFileName(scope, remotePath, sizeBytes, modifiedMillis)
        return File(directory, name)
    }

    companion object {
        /**
         * How much disk the previews may hold.
         *
         * It is `cacheDir`, so the platform may empty it under storage pressure, and it holds images
         * that can be fetched again — a phone does not need a second copy of a photo library on disk.
         * The budget is deliberately a few images rather than a few hundred.
         */
        const val CACHE_BUDGET_BYTES: Long = 64L * 1024 * 1024
    }
}

/** One cached preview, as the eviction policy sees it: no file system access, only what it needs. */
internal data class PreviewEntry(val name: String, val sizeBytes: Long, val lastModifiedMillis: Long)

/**
 * Which entries have to go for the cache to fit inside [budgetBytes], least recently used first.
 *
 * Split out from [ImagePreviewCache.trim] so the policy can be tested without a clock: file
 * modification times have one-second granularity on most file systems, which makes any ordering test
 * written against real files a coin toss.
 */
internal fun previewsToEvict(entries: List<PreviewEntry>, budgetBytes: Long): List<PreviewEntry> {
    var total = entries.sumOf { it.sizeBytes }
    if (total <= budgetBytes) {
        return emptyList()
    }
    val evicted = mutableListOf<PreviewEntry>()
    for (entry in entries.sortedBy { it.lastModifiedMillis }) {
        if (total <= budgetBytes) {
            break
        }
        evicted += entry
        total -= entry.sizeBytes
    }
    return evicted
}

/**
 * The file name of one cached preview.
 *
 * The key is the whole identity of the remote file, including the revision: a preview is only valid
 * for the exact revision of the exact file on the exact server, and nothing weaker than that may be
 * reused, because the result is shown to the user as "this is what your file looks like".
 */
internal fun previewCacheFileName(
    scope: String,
    remotePath: String,
    sizeBytes: Long?,
    modifiedMillis: Long?,
): String {
    val key = "$scope\n$remotePath\n${sizeBytes ?: UNKNOWN}\n${modifiedMillis ?: UNKNOWN}"
    return "preview-${sha1Hex(key)}$PREVIEW_SUFFIX"
}

/** A 160-bit digest as hex: readable, fixed length and free of anything a file name cannot hold. */
private fun sha1Hex(value: String): String =
    MessageDigest.getInstance("SHA-1").digest(value.toByteArray(Charsets.UTF_8))
        .joinToString("") { byte -> "%02x".format(byte) }

/** What marks a file in the cache directory as a preview, and so as something [ImagePreviewCache.trim] may delete. */
private const val PREVIEW_SUFFIX = ".preview"

/**
 * The directory name the previews live under, inside whatever cache directory the caller has.
 *
 * A constant rather than a string at the composition root so that "which folder is the app allowed to
 * throw away" is answered next to the code that throws it away.
 */
const val PREVIEW_CACHE_DIRECTORY = "previews"

/** The placeholder used when the server reported no size, so the two absences cannot collide. */
private const val UNKNOWN = -1L
