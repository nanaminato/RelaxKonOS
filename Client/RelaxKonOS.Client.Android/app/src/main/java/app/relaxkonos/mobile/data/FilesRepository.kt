package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.DownloadSink
import app.relaxkonos.mobile.core.net.FileElevationCapabilities
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import java.io.InputStream

/**
 * Remote file operations.
 *
 * Every call that can touch a protected path goes through [ElevationRepository.withPathElevation], so
 * a protected path produces exactly one `elevation-required` round trip, one user authorization and
 * one retry. The repository never decides the password itself — it asks [provider], which is the
 * elevation dialog.
 */
class FilesRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val elevations: ElevationRepository,
) {
    suspend fun list(path: String, provider: ElevationAnswerProvider): ApiResult<DirectoryListing> {
        // An empty path means "list the roots", which the server serves without elevation.
        if (path.isBlank()) {
            return session.authenticated { serverUrl, accessToken -> gateway.listDirectory(serverUrl, accessToken, path) }
        }
        return elevations.withPathElevation(
            path = path,
            capability = FileElevationCapabilities.READ,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.listDirectory(serverUrl, accessToken, path) }
    }

    suspend fun properties(path: String): ApiResult<RemoteFileProperties> =
        session.authenticated { serverUrl, accessToken -> gateway.fileProperties(serverUrl, accessToken, path) }

    suspend fun createDirectory(path: String, provider: ElevationAnswerProvider): ApiResult<Unit> =
        elevations.withPathElevation(
            // The new directory must be covered by the grant, so the parent scope is elevated.
            path = parentOf(path),
            capability = FileElevationCapabilities.CREATE_DIRECTORY,
            includeDescendants = true,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.createDirectory(serverUrl, accessToken, path) }

    suspend fun delete(path: String, provider: ElevationAnswerProvider): ApiResult<Unit> =
        elevations.withPathElevation(
            path = path,
            capability = FileElevationCapabilities.DELETE,
            includeDescendants = true,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.delete(serverUrl, accessToken, path) }

    suspend fun rename(sourcePath: String, newName: String, provider: ElevationAnswerProvider): ApiResult<Unit> =
        elevations.withPathElevation(
            path = sourcePath,
            capability = FileElevationCapabilities.RENAME,
            relatedPaths = listOf(join(parentOf(sourcePath), newName)),
            provider = provider,
        ) { serverUrl, accessToken -> gateway.rename(serverUrl, accessToken, sourcePath, newName) }

    suspend fun move(sourcePath: String, destinationPath: String, provider: ElevationAnswerProvider): ApiResult<Unit> =
        transfer(sourcePath, destinationPath, FileElevationCapabilities.MOVE, provider) { serverUrl, accessToken ->
            gateway.move(serverUrl, accessToken, sourcePath, destinationPath)
        }

    suspend fun copy(sourcePath: String, destinationPath: String, provider: ElevationAnswerProvider): ApiResult<Unit> =
        transfer(sourcePath, destinationPath, FileElevationCapabilities.COPY, provider) { serverUrl, accessToken ->
            gateway.copy(serverUrl, accessToken, sourcePath, destinationPath)
        }

    /** Uploads a system document stream without ever relying on a filesystem path. */
    suspend fun upload(
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        provider: ElevationAnswerProvider,
        onProgress: ((Long) -> Unit)? = null,
    ): ApiResult<Unit> = elevations.withPathElevation(
        path = targetDirectoryPath,
        capability = FileElevationCapabilities.UPLOAD,
        includeDescendants = true,
        provider = provider,
    ) { serverUrl, accessToken ->
        gateway.upload(serverUrl, accessToken, targetDirectoryPath, fileName, source, contentLength, onProgress)
    }

    /**
     * Streams one remote file into [target]. The server grants read access to the file's own path.
     *
     * The destination is opened by the transport only after the request succeeds, and committing or
     * discarding it stays with the caller: a transfer that the server refuses must not leave a file
     * behind (`DownloadTarget`).
     */
    suspend fun download(
        path: String,
        target: DownloadSink,
        provider: ElevationAnswerProvider,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)? = null,
    ): ApiResult<Long> =
        elevations.withPathElevation(
            path = path,
            capability = FileElevationCapabilities.READ,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.download(serverUrl, accessToken, path, target, onProgress) }

    /**
     * Fetches the server's small rendering of one image.
     *
     * It asks for the same capability a download does, because it reads the same bytes: a protected
     * file answers `elevation-required` here exactly as it would there, and going through the same
     * coordinator means a caller that has already declined is not asked a second time for the same
     * picture.
     */
    suspend fun thumbnail(
        path: String,
        maxEdge: Int,
        provider: ElevationAnswerProvider,
    ): ApiResult<ByteArray> =
        elevations.withPathElevation(
            path = path,
            capability = FileElevationCapabilities.READ,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.thumbnail(serverUrl, accessToken, path, maxEdge) }

    /** The parent directory of a canonical path, used to scope grants for newly created entries. */
    internal fun parentOf(path: String): String {
        if (isDriveRoot(path)) {
            return path
        }
        val trimmed = path.trimEnd('/', '\\')
        val separator = trimmed.lastIndexOfAny(charArrayOf('/', '\\'))
        return when {
            separator < 0 -> ROOT
            separator == 0 -> ROOT
            // Keep the separator in a Windows drive root: `C:\\work` has parent `C:\\`, not `C:`.
            separator == 2 && trimmed.length >= 3 && trimmed[1] == ':' -> trimmed.substring(0, separator + 1)
            else -> trimmed.substring(0, separator)
        }
    }

    /**
     * The parent location used by the file-browser UI.
     *
     * A Windows drive root is its own filesystem parent for operations such as creating a
     * directory, but its navigation parent is the virtual drive list (represented by an empty
     * path). Keeping those two meanings separate lets the user return to the drive picker without
     * widening any elevation scope.
     */
    internal fun navigationParentOf(path: String): String =
        if (isDriveRoot(path)) "" else parentOf(path)

    private suspend fun transfer(
        sourcePath: String,
        destinationPath: String,
        capability: String,
        provider: ElevationAnswerProvider,
        operation: suspend (String, String) -> ApiResult<Unit>,
    ): ApiResult<Unit> = elevations.withPathElevation(
        // Both parent directories must be in the elevated scope: source is read/removed and the
        // destination is created. Sending the directory scopes also avoids granting a whole drive.
        path = parentOf(sourcePath),
        capability = capability,
        relatedPaths = listOf(parentOf(destinationPath)),
        includeDescendants = true,
        provider = provider,
        call = operation,
    )

    internal fun childOf(parent: String, name: String): String = when {
        parent == ROOT -> "$ROOT$name"
        parent.endsWith("/") || parent.endsWith("\\") -> parent + name
        parent.contains('\\') && !parent.contains('/') -> "$parent\\$name"
        else -> "$parent/$name"
    }

    private fun join(parent: String, name: String): String = childOf(parent, name)

    private fun isDriveRoot(path: String): Boolean =
        path.length == 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\')

    private companion object {
        const val ROOT = "/"
    }
}
