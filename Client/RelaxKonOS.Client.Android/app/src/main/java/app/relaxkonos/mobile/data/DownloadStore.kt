package app.relaxkonos.mobile.data

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.DownloadSink
import java.io.IOException
import java.io.OutputStream

/**
 * The destination of one download, including what the platform needs *after* the last byte.
 *
 * A download is a write into storage the user owns rather than into the app's sandbox, so it is three
 * steps instead of one: open the destination, write it, then either keep it or remove it. Keeping
 * them separate is what lets a failed or cancelled transfer leave nothing behind — a half-written
 * file in the shared Downloads collection would otherwise be a corrupt entry for every app on the
 * device to see.
 */
interface DownloadTarget : DownloadSink {
    /** The name the file ends up with; the platform may adjust it to avoid a collision. */
    val displayName: String

    /** Where it landed, in the words the confirmation message uses. */
    val location: String

    /** Keeps the bytes. Called exactly once, after a successful transfer. */
    fun commit()

    /** Removes the destination after a failed or cancelled transfer. Never called after [commit]. */
    fun discard()
}

/**
 * Resolves where a download lands on this device.
 *
 * **API 29 and later** write through `MediaStore` into the shared Downloads collection, under
 * `Download/RelaxKonOS`. That needs no permission and shows no dialog: the file is in the user's
 * Downloads the moment the transfer finishes, and it stays there if the app is uninstalled. A
 * download that ends in a private cache handing the bytes to a share sheet never reaches the device
 * at all — the user has to pick a destination from a chooser for every single file
 * (`Product.Design.md` §7).
 *
 */
class DownloadStore(private val context: Context) {
    /** Creates the destination for one download. No file exists until the first write. */
    fun create(displayName: String): DownloadTarget {
        val name = downloadFileName(displayName).ifBlank { context.getString(R.string.files_download_default_name) }
        return mediaStoreTarget(name)
    }

    private fun mediaStoreTarget(name: String): DownloadTarget {
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, downloadMimeType(name))
            put(MediaStore.MediaColumns.RELATIVE_PATH, RELATIVE_DIRECTORY)
            // Pending until commit(): nothing else can open a file that is still being written.
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("MediaStore refused a download target for $name.")
        // A name collision is resolved by MediaStore appending a counter, and only it knows the
        // result. Re-reading the row is what keeps the confirmation message from naming a file that
        // does not exist.
        val actual = resolver.resolvedDisplayName(uri) ?: name
        return MediaStoreTarget(resolver, uri, actual)
    }

}

/** Writes into a `MediaStore` row; the row is what makes the file visible to the rest of the device. */
private class MediaStoreTarget(
    private val resolver: ContentResolver,
    private val uri: Uri,
    override val displayName: String,
) : DownloadTarget {
    override val location: String get() = "$RELATIVE_DIRECTORY/$displayName"

    override fun open(): OutputStream =
        resolver.openOutputStream(uri) ?: throw IOException("MediaStore produced no stream for $location.")

    override fun commit() {
        resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
    }

    override fun discard() {
        resolver.delete(uri, null, null)
    }
}

internal fun ContentResolver.resolvedDisplayName(uri: Uri): String? = runCatching {
    query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) cursor.getString(0) else null
    }
}.getOrNull()?.takeIf { it.isNotBlank() }

/** The last path component of a server-provided name: a file name may not contain a separator. */
internal fun downloadFileName(name: String): String = name.substringAfterLast('/').substringAfterLast('\\')

/**
 * The MIME type the platform will file this download under.
 *
 * It matters beyond bookkeeping: it decides which app the system offers when the user opens the file
 * from Downloads, so an unknown extension is declared as opaque bytes rather than guessed at.
 */
internal fun downloadMimeType(name: String): String =
    MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
        ?: "application/octet-stream"

/** Where a download is placed in the shared Downloads collection: no leading slash, no trailing one. */
private const val RELATIVE_DIRECTORY = "Download/RelaxKonOS"
