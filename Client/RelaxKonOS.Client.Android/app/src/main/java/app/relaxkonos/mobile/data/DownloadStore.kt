package app.relaxkonos.mobile.data

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.DownloadSink
import java.io.File
import java.io.FileOutputStream
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
 * (`RelaxKonOS.Mobile.Design.md` §7).
 *
 * **API 23–28** have no such write path. Reaching the public `Download/` folder there means either the
 * broad `WRITE_EXTERNAL_STORAGE` permission or a storage-access-framework picker, and the mobile
 * design rules out both — no wide storage permission, and no detour in front of a plain download
 * (same section). Those versions therefore land in the app's own external `Download` directory: real
 * files on the device's shared storage, visible to a desktop over USB, and removed with the app when
 * it is uninstalled. The confirmation message always names the actual location, so which of the two
 * paths was taken is something the user reads rather than something to guess at.
 */
class DownloadStore(private val context: Context) {
    /** Creates the destination for one download. No file exists until the first write. */
    fun create(displayName: String): DownloadTarget {
        val name = downloadFileName(displayName).ifBlank { context.getString(R.string.files_download_default_name) }
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            mediaStoreTarget(name)
        } else {
            legacyTarget(name)
        }
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

    private fun legacyTarget(name: String): DownloadTarget {
        val directory = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir, DIRECTORY_NAME)
        directory.mkdirs()
        val file = File(directory, uniqueDownloadName(name) { File(directory, it).exists() })
        return LegacyTarget(file)
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

/** Writes into a plain file; there is no pending state to clear and nothing to publish. */
private class LegacyTarget(private val file: File) : DownloadTarget {
    override val displayName: String get() = file.name
    override val location: String get() = file.absolutePath

    override fun open(): OutputStream = FileOutputStream(file)

    override fun commit() = Unit

    override fun discard() {
        file.delete()
    }
}

private fun ContentResolver.resolvedDisplayName(uri: Uri): String? = runCatching {
    query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) cursor.getString(0) else null
    }
}.getOrNull()?.takeIf { it.isNotBlank() }

/** The last path component of a server-provided name: a file name may not contain a separator. */
internal fun downloadFileName(name: String): String = name.substringAfterLast('/').substringAfterLast('\\')

/**
 * The first free name for [name]: `report.txt`, `report (1).txt`, `report (2).txt`, …
 *
 * A download never overwrites what is already on the device. The user asked for a copy of a remote
 * file, and silently replacing an older download is the opposite of making one.
 */
internal fun uniqueDownloadName(name: String, taken: (String) -> Boolean): String {
    if (!taken(name)) {
        return name
    }
    val dot = name.lastIndexOf('.')
    // `dot > 0` keeps a dotfile intact: `.bashrc` has no extension to split off.
    val stem = if (dot > 0) name.substring(0, dot) else name
    val extension = if (dot > 0) name.substring(dot) else ""
    var index = 1
    while (true) {
        val candidate = "$stem ($index)$extension"
        if (!taken(candidate)) {
            return candidate
        }
        index++
    }
}

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

private const val DIRECTORY_NAME = "RelaxKonOS"
