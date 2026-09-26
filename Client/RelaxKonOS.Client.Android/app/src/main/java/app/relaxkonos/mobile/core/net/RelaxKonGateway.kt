package app.relaxkonos.mobile.core.net

import java.io.InputStream
import java.io.OutputStream

/**
 * Where the body of a download is written.
 *
 * The transport knows how to fetch bytes and nothing about where they belong, which is what keeps
 * `MediaStore`, the cache and any future destination out of the HTTP layer. The stream is opened only
 * after the server has accepted the request, so a refused download never creates a file, and it
 * belongs to the caller, which closes it.
 */
fun interface DownloadSink {
    fun open(): OutputStream
}

/**
 * The REST surface the app depends on. Declared as an interface so `AuthSession`, the repositories
 * and their unit tests can substitute a fake instead of a live server.
 *
 * The implementation is [RelaxKonApi]; route names and payload shapes stay owned by that class.
 */
interface RelaxKonGateway {
    suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>>
    suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot>
    suspend fun deploymentRuntime(serverUrl: String, accessToken: String): ApiResult<DeploymentRuntime>

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

    /**
     * Opens a resumable session for one file of [length] bytes.
     *
     * [idempotencyKey] is not optional: creating a session is the one step that allocates a staging
     * file on the server, so a retry after a lost response must return the same session rather than
     * leak a second one. The server rejects a request that arrives without one.
     *
     * A protected destination answers `elevation-required` here and only here, which is what keeps the
     * authorization dialog from appearing in the middle of a transfer.
     */
    suspend fun createUploadSession(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        length: Long,
        lastModifiedMillis: Long?,
        idempotencyKey: String,
    ): ApiResult<UploadSession>

    /** Reads the authoritative offset of [uploadId]. The only way to resolve any doubt about what arrived. */
    suspend fun uploadSession(serverUrl: String, accessToken: String, uploadId: String): ApiResult<UploadSession>

    /**
     * Sends exactly [chunkLength] bytes read from [source] as the chunk at [offset].
     *
     * [onInFlight] reports bytes handed to the socket, which the caller turns into a progress estimate.
     * The chunk is streamed: nothing here holds a whole chunk in memory on the transport's behalf.
     */
    suspend fun sendUploadChunk(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        offset: Long,
        chunkLength: Long,
        source: InputStream,
        onInFlight: ((Long) -> Unit)? = null,
    ): UploadChunkResult

    /**
     * Publishes a fully received session as the destination file. The only step that creates or
     * replaces anything the user can see: a session that was never committed leaves no trace.
     */
    suspend fun commitUpload(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        contentHash: String? = null,
    ): ApiResult<Unit>

    /** Abandons a session. Always reported as success when the session is already gone. */
    suspend fun abortUpload(serverUrl: String, accessToken: String, uploadId: String): ApiResult<Unit>

    suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot>

    suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
    ): ApiResult<ProcessPage>

    suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, force: Boolean): ApiResult<Unit>

    /** Streams a remote file into [sink]. [onProgress] receives written bytes and total bytes when known. */
    suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        sink: DownloadSink,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)? = null,
    ): ApiResult<Long>

    /**
     * Fetches the server's small rendering of [path]: an image whose longest edge is at most
     * [maxEdge] pixels, returned as the encoded bytes.
     *
     * Bytes rather than a download destination, because there is only ever one destination for a
     * thumbnail — memory, for as long as it takes to decode it. It exists so that a picture can be
     * shown before a whole photograph has been transferred.
     *
     * A file the server cannot draw answers with the `thumbnail-unsupported` problem, which is a
     * normal answer rather than a failure: there is no thumbnail, and the caller fetches the file.
     */
    suspend fun thumbnail(
        serverUrl: String,
        accessToken: String,
        path: String,
        maxEdge: Int,
    ): ApiResult<ByteArray>
}
