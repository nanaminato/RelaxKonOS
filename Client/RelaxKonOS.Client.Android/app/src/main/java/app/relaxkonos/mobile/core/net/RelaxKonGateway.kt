package app.relaxkonos.mobile.core.net

import java.io.File
import java.io.InputStream

/**
 * The REST surface the app depends on. Declared as an interface so `AuthSession`, the repositories
 * and their unit tests can substitute a fake instead of a live server.
 *
 * The implementation is [RelaxKonApi]; route names and payload shapes stay owned by that class.
 */
interface RelaxKonGateway {
    suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession>

    suspend fun refresh(serverUrl: String, refreshToken: String): ApiResult<AuthTokens>

    suspend fun logout(serverUrl: String, accessToken: String, refreshToken: String): ApiResult<Unit>

    suspend fun requestElevation(
        serverUrl: String,
        accessToken: String,
        capability: String,
        target: String,
        password: CharArray?,
        administratorUsername: String?,
    ): ApiResult<ElevationGrant>

    /** One-shot file elevation request through the file-specific entry point. */
    suspend fun requestFileElevation(
        serverUrl: String,
        accessToken: String,
        path: String,
        capability: String?,
        password: CharArray?,
        relatedPaths: List<String>,
        includeDescendants: Boolean,
        administratorUsername: String?,
    ): ApiResult<FileElevationGrant>

    suspend fun listDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<DirectoryListing>

    suspend fun fileProperties(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteFileProperties>

    suspend fun createDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<Unit>

    suspend fun delete(serverUrl: String, accessToken: String, path: String): ApiResult<Unit>

    suspend fun rename(serverUrl: String, accessToken: String, sourcePath: String, newName: String): ApiResult<Unit>

    suspend fun move(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit>

    suspend fun copy(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit>

    /** Uploads one SAF-owned stream. [onProgress] receives bytes accepted by the request body. */
    suspend fun upload(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        onProgress: ((Long) -> Unit)? = null,
    ): ApiResult<Unit>

    suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot>

    suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
    ): ApiResult<ProcessPage>

    suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, force: Boolean): ApiResult<Unit>

    /** Downloads to an app-owned cache file. [onProgress] receives written bytes and total bytes when known. */
    suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        target: File,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)? = null,
    ): ApiResult<Long>
}
