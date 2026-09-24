package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.DownloadSink
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.security.MessageDigest

/**
 * The destination of one preview download.
 *
 * A preview is a copy made *for looking at*, not a download: it lands in the app's own cache, and the
 * app may delete it again without asking. That is the whole difference from `DownloadTarget` — one ends
 * in a file the user keeps in Downloads and expects to find tomorrow, the other in bytes that are only
 * ever read by the image viewer — and it is why the two do not share a type.
 *
 * Bytes are written straight to the final name rather than to a temporary file that is renamed on
 * success, because a rename cannot tell a complete file from a truncated one. What can is the size the
 * server already reported: [ImagePreviewCache.cached] only accepts a file whose length matches, so a
 * transfer cut short by a crash is treated as a miss instead of being decoded into a half image.
 */
class PreviewTarget(val file: File) : DownloadSink {
    override fun open(): OutputStream = FileOutputStream(file)
}

/**
 * Cache of remote images the user has looked at.
 *
 * ## Identity, not names
 *
 * An entry is keyed by the server, the remote path and the file's size and modification time, hashed
 * into a fixed-length name. Three things follow from that, and all three are the reason for it:
 *
 *  - A remote path is not a file name: it can contain `/`, `\`, `:` and anything else a Windows path
 *    is allowed to hold, none of which can appear in one.
 *  - A file that changed on the host produces a different key, so an edited image is re-fetched
 *    instead of being shown from a stale copy.
 *  - Two servers are two namespaces, so the same path on two hosts is two entries.
 *
 * ## Size
 *
 * The cache is bounded, and the bound is enforced by [trim] on the oldest entries. Eviction can
 * therefore delete a file whose bitmap is still on screen; that costs a later re-read of a file that
 * is already decoded in memory, which is to say nothing the user can see.
 */
class ImagePreviewCache(private val directory: File) {
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

    /** Opens the destination for a fresh copy. Any earlier partial copy of the same revision is replaced. */
    fun create(scope: String, remotePath: String, sizeBytes: Long?, modifiedMillis: Long?): PreviewTarget {
        directory.mkdirs()
        trim()
        return PreviewTarget(fileNameFor(scope, remotePath, sizeBytes, modifiedMillis))
    }

    /** Records that [file] was used just now, so the least recently *viewed* image is evicted first. */
    fun touch(file: File) {
        file.setLastModified(System.currentTimeMillis())
    }

    /** Deletes the least recently used entries until the cache fits inside [budgetBytes]. */
    fun trim(budgetBytes: Long = CACHE_BUDGET_BYTES) {
        val files = directory.listFiles().orEmpty()
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
