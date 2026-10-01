package app.relaxkonos.mobile.servercenter

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** SAF-only tree inspection. Provider IDs are never treated as local filesystem paths. */
data class SshUploadDocument(val uri: Uri, val relativePath: String, val isDirectory: Boolean, val size: Long?)
class AndroidSshDocuments(private val resolver: ContentResolver) {
    suspend fun files(uris: List<Uri>): List<SshUploadDocument> = withContext(Dispatchers.IO) {
        require(uris.isNotEmpty() && uris.size <= SshFileTransferRules.MAX_ENTRIES) { "transfer-limit" }
        val items = uris.map { uri ->
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
                check(cursor.moveToFirst()) { "source-changed" }
                val name = cursor.getString(0); require(SshFileTransferRules.safeName(name)) { "invalid-name" }
                SshUploadDocument(uri, name, false, if (cursor.isNull(1)) null else cursor.getLong(1))
            } ?: error("source-changed")
        }
        require(items.map { it.relativePath }.distinct().size == items.size) { "destination-exists" }
        checkSizes(items); items
    }
    suspend fun tree(tree: Uri): List<SshUploadDocument> = withContext(Dispatchers.IO) {
        val result = mutableListOf<SshUploadDocument>()
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        fun inspect(id: String, parent: String?, depth: Int) {
            require(depth <= SshFileTransferRules.MAX_DEPTH && result.size < SshFileTransferRules.MAX_ENTRIES) { "transfer-limit" }
            val uri = DocumentsContract.buildDocumentUriUsingTree(tree, id)
            val item = resolver.query(uri, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_SIZE), null, null, null)?.use { cursor ->
                check(cursor.moveToFirst()) { "source-changed" }
                val name = cursor.getString(0); require(SshFileTransferRules.safeName(name)) { "invalid-name" }
                SshUploadDocument(uri, if (parent == null) name else "$parent/$name",
                    cursor.getString(1) == DocumentsContract.Document.MIME_TYPE_DIR, if (cursor.isNull(2)) null else cursor.getLong(2))
            } ?: error("source-changed")
            require(result.none { it.relativePath == item.relativePath }) { "source-changed" }
            result += item
            if (item.isDirectory) resolver.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree, id), arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID), null, null, null)?.use { cursor ->
                while (cursor.moveToNext()) inspect(cursor.getString(0), item.relativePath, depth + 1)
            } ?: error("source-changed")
        }
        inspect(rootId, null, 0); checkSizes(result); result
    }
    private fun checkSizes(items: List<SshUploadDocument>) {
        var total = 0L
        items.filterNot { it.isDirectory }.forEach { item -> item.size?.let {
            require(it >= 0 && it <= SshFileTransferRules.MAX_BYTES - total) { "transfer-limit" }; total += it
        } }
    }
}
