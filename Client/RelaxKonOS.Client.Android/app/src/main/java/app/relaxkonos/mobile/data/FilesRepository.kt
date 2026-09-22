package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.FileElevationCapabilities
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import java.io.File

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

    /** Downloads into [target]. The server grants read access to the file's own path. */
    suspend fun download(path: String, target: File, provider: ElevationAnswerProvider): ApiResult<Long> =
        elevations.withPathElevation(
            path = path,
            capability = FileElevationCapabilities.READ,
            provider = provider,
        ) { serverUrl, accessToken -> gateway.download(serverUrl, accessToken, path, target) }

    /** The parent directory of a canonical path, used to scope grants for newly created entries. */
    internal fun parentOf(path: String): String {
        val trimmed = path.trimEnd('/', '\\')
        val separator = trimmed.lastIndexOfAny(charArrayOf('/', '\\'))
        return if (separator <= 0) ROOT else trimmed.substring(0, separator)
    }

    private fun join(parent: String, name: String): String = when {
        parent == ROOT -> "$ROOT$name"
        parent.endsWith("/") || parent.endsWith("\\") -> parent + name
        else -> "$parent/$name"
    }

    private companion object {
        const val ROOT = "/"
    }
}
