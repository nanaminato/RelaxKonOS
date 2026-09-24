package app.relaxkonos.mobile.data

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import java.io.FileInputStream
import java.io.FileNotFoundException
import java.io.FilterInputStream
import java.io.IOException
import java.io.InputStream

/**
 * Opens the documents the picker handed back.
 *
 * Everything here is the provider's work, not ours: a `query` against a cloud-backed document can take
 * hundreds of milliseconds and opening it seconds, so every caller runs this on `Dispatchers.IO`. Name
 * and size are best-effort — a provider that refuses either still gets to upload, with the transfer
 * card falling back to a generic label and an indeterminate bar.
 */
class AndroidUploadDocuments(context: Context) : UploadDocumentOpener {
    private val resolver: ContentResolver = context.applicationContext.contentResolver

    override fun open(uriString: String): PickedDocument? {
        val uri = runCatching { Uri.parse(uriString) }.getOrNull() ?: return null
        if (uri.scheme.isNullOrBlank()) return null
        val metadata = metadata(uri)
        // The document must be openable at all, otherwise an entry pointing at it is worthless: a
        // resume is only ever offered for bytes that can actually be read again.
        if (runCatching { resolver.openInputStream(uri)?.close() }.getOrNull() == null) return null
        return PickedDocument(
            uri = uriString,
            displayName = metadata.name.takeIf { it.isNotBlank() } ?: uri.lastPathSegment.orEmpty(),
            length = metadata.length,
            lastModifiedMillis = metadata.lastModified,
            open = { resolver.openInputStream(uri) ?: throw IOException("document is not readable") },
            openAt = { offset -> openAt(uri, offset) },
        )
    }

    /**
     * Opens a stream positioned at [offset].
     *
     * The descriptor is what makes this possible: a plain `openInputStream` gives a pipe for a
     * cloud-backed document and a pipe cannot be positioned. A provider that cannot answer with a
     * seekable descriptor throws [SeekUnsupportedException], which the stager turns into "copy this
     * into the cache first".
     */
    private fun openAt(uri: Uri, offset: Long): InputStream {
        val descriptor = try {
            resolver.openFileDescriptor(uri, "r") ?: throw SeekUnsupportedException("no descriptor for $uri")
        } catch (error: FileNotFoundException) {
            throw SeekUnsupportedException("$uri cannot be opened as a file: ${error.message}")
        } catch (error: IllegalArgumentException) {
            throw SeekUnsupportedException("$uri refuses the read mode: ${error.message}")
        } catch (error: SecurityException) {
            throw SeekUnsupportedException("$uri is no longer authorized: ${error.message}")
        }
        val stream = FileInputStream(descriptor.fileDescriptor)
        try {
            stream.channel.position(offset)
        } catch (error: IOException) {
            // A pipe cannot seek. This is the answer, not a failure: it means "stage me first".
            runCatching { stream.close() }
            runCatching { descriptor.close() }
            throw SeekUnsupportedException("$uri is not seekable: ${error.message}")
        }
        return DescriptorInputStream(descriptor, stream)
    }

    private class DocumentMetadata(val name: String, val length: Long?, val lastModified: Long?)

    private fun metadata(uri: Uri): DocumentMetadata {
        var name = ""
        var size: Long? = null
        var lastModified: Long? = null
        runCatching {
            // `last_modified` is a DocumentsProvider column and not every provider accepts it in a
            // projection, so the query is retried without it rather than failing the whole upload.
            val columns = arrayOf(
                OpenableColumns.DISPLAY_NAME,
                OpenableColumns.SIZE,
                DocumentsContract.Document.COLUMN_LAST_MODIFIED,
            )
            val cursor = resolver.query(uri, columns, null, null, null)
                ?: resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)
            cursor?.use { rows ->
                if (!rows.moveToFirst()) return@use
                val nameIndex = rows.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (nameIndex >= 0) name = rows.getString(nameIndex).orEmpty()
                val sizeIndex = rows.getColumnIndex(OpenableColumns.SIZE)
                if (sizeIndex >= 0 && !rows.isNull(sizeIndex)) size = rows.getLong(sizeIndex)
                val modifiedIndex = rows.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                if (modifiedIndex >= 0 && !rows.isNull(modifiedIndex)) lastModified = rows.getLong(modifiedIndex)
            }
        }
        val length = size?.takeIf { it >= 0L }
            ?: runCatching { resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } }
                .getOrNull()?.takeIf { it >= 0L }
        return DocumentMetadata(name, length, lastModified)
    }
}

/**
 * A stream that keeps the descriptor it was opened from alive for exactly as long as itself.
 *
 * A `FileInputStream` built from a `ParcelFileDescriptor`'s raw descriptor does not own it, so closing
 * the stream alone would leak one descriptor per chunk — four hundred of them for a 3 GB file. Tying
 * the two lifetimes together here is what stops that, and it means callers can treat the stream as an
 * ordinary `InputStream`.
 */
private class DescriptorInputStream(
    private val descriptor: ParcelFileDescriptor,
    stream: FileInputStream,
) : FilterInputStream(stream) {
    override fun close() {
        try {
            super.close()
        } finally {
            runCatching { descriptor.close() }
        }
    }
}
