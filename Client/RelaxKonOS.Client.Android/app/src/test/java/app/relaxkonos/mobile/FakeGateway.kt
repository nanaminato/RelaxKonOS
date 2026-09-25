package app.relaxkonos.mobile

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.DownloadSink
import app.relaxkonos.mobile.core.net.ElevationGrant
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.LoginSession
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerDescriptor
import app.relaxkonos.mobile.core.net.UploadChunkResult
import app.relaxkonos.mobile.core.net.UploadSession
import java.io.InputStream

/**
 * Scriptable [RelaxKonGateway].
 *
 * Each operation has a handler the test sets, plus counters for the calls that matter to the rules under
 * test. A handler that was not set throws, so a test cannot pass because an unexpected call quietly
 * returned a default.
 */
class FakeGateway : RelaxKonGateway {
    var onLogin: (suspend (String, String, CharArray) -> ApiResult<LoginSession>)? = null
    var onRefresh: (suspend (String, String) -> ApiResult<AuthTokens>)? = null
    var onLogout: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onElevation: (suspend (String, String, String, String, CharArray?, String?) -> ApiResult<ElevationGrant>)? = null
    var onFileElevation: (suspend (String, String, String, String?, CharArray?, List<String>, Boolean, String?) -> ApiResult<FileElevationGrant>)? = null
    var onListDirectory: (suspend (String, String, String) -> ApiResult<DirectoryListing>)? = null
    var onFileProperties: (suspend (String, String, String) -> ApiResult<RemoteFileProperties>)? = null
    var onCreateDirectory: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onDelete: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onRename: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onMove: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onCopy: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onUpload: (suspend (String, String, String, String, InputStream, Long?, ((Long) -> Unit)?) -> ApiResult<Unit>)? = null
    var onPerformance: (suspend (String, String) -> ApiResult<PerformanceSnapshot>)? = null
    var onProcesses: (suspend (String, String, Int, Int, String?) -> ApiResult<ProcessPage>)? = null
    var onKill: (suspend (String, String, Int, Boolean) -> ApiResult<Unit>)? = null
    var onDownload: (suspend (String, String, String, DownloadSink, ((Long, Long?) -> Unit)?) -> ApiResult<Long>)? = null
    var onThumbnail: (suspend (String, String, String, Int) -> ApiResult<ByteArray>)? = null
    var onCreateUploadSession:
        (suspend (String, String, String, String, Long, Long?, String) -> ApiResult<UploadSession>)? = null
    var onUploadSession: (suspend (String, String, String) -> ApiResult<UploadSession>)? = null
    var onSendUploadChunk:
        (suspend (String, String, String, Long, Long, InputStream, ((Long) -> Unit)?) -> UploadChunkResult)? = null
    var onCommitUpload: (suspend (String, String, String, String?) -> ApiResult<Unit>)? = null
    var onAbortUpload: (suspend (String, String, String) -> ApiResult<Unit>)? = null

    var loginCount = 0
        private set

    var refreshCount = 0
        private set

    var elevationCount = 0
        private set

    var logoutCount = 0
        private set

    val elevationTargets = mutableListOf<String>()
    val elevationAccounts = mutableListOf<String?>()
    val listDirectoryPaths = mutableListOf<String>()
    val killCalls = mutableListOf<Pair<Int, Boolean>>()

    /** Abandoned sessions, so a test can assert that cancel really told the server. */
    val abortedUploadIds = mutableListOf<String>()
    val committedUploadIds = mutableListOf<String>()

    /** Offsets requested through the authoritative read, so a test can count resynchronisations. */
    val uploadSessionReads = mutableListOf<String>()

    override suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession> {
        loginCount++
        return requireHandler(onLogin, "login")(serverUrl, identifier, password)
    }

    override suspend fun refresh(serverUrl: String, refreshToken: String): ApiResult<AuthTokens> {
        refreshCount++
        return requireHandler(onRefresh, "refresh")(serverUrl, refreshToken)
    }

    override suspend fun logout(serverUrl: String, accessToken: String, refreshToken: String): ApiResult<Unit> {
        logoutCount++
        return requireHandler(onLogout, "logout")(serverUrl, accessToken, refreshToken)
    }

    override suspend fun requestElevation(
        serverUrl: String,
        accessToken: String,
        capability: String,
        target: String,
        password: CharArray?,
        administratorUsername: String?,
    ): ApiResult<ElevationGrant> {
        elevationCount++
        elevationTargets += target
        elevationAccounts += administratorUsername
        return requireHandler(onElevation, "requestElevation")(serverUrl, accessToken, capability, target, password, administratorUsername)
    }

    override suspend fun requestFileElevation(
        serverUrl: String,
        accessToken: String,
        path: String,
        capability: String?,
        password: CharArray?,
        relatedPaths: List<String>,
        includeDescendants: Boolean,
        administratorUsername: String?,
    ): ApiResult<FileElevationGrant> {
        elevationCount++
        elevationTargets += path
        elevationAccounts += administratorUsername
        return requireHandler(onFileElevation, "requestFileElevation")(
            serverUrl,
            accessToken,
            path,
            capability,
            password,
            relatedPaths,
            includeDescendants,
            administratorUsername,
        )
    }

    override suspend fun listDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<DirectoryListing> {
        listDirectoryPaths += path
        return requireHandler(onListDirectory, "listDirectory")(serverUrl, accessToken, path)
    }

    override suspend fun fileProperties(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteFileProperties> =
        requireHandler(onFileProperties, "fileProperties")(serverUrl, accessToken, path)

    override suspend fun createDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        requireHandler(onCreateDirectory, "createDirectory")(serverUrl, accessToken, path)

    override suspend fun delete(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        requireHandler(onDelete, "delete")(serverUrl, accessToken, path)

    override suspend fun rename(serverUrl: String, accessToken: String, sourcePath: String, newName: String): ApiResult<Unit> =
        requireHandler(onRename, "rename")(serverUrl, accessToken, sourcePath, newName)

    override suspend fun move(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> =
        requireHandler(onMove, "move")(serverUrl, accessToken, sourcePath, destinationPath)

    override suspend fun copy(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> =
        requireHandler(onCopy, "copy")(serverUrl, accessToken, sourcePath, destinationPath)

    override suspend fun upload(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        onProgress: ((Long) -> Unit)?,
    ): ApiResult<Unit> = requireHandler(onUpload, "upload")(
        serverUrl,
        accessToken,
        targetDirectoryPath,
        fileName,
        source,
        contentLength,
        onProgress,
    )

    override suspend fun createUploadSession(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        length: Long,
        lastModifiedMillis: Long?,
        idempotencyKey: String,
    ): ApiResult<UploadSession> = requireHandler(onCreateUploadSession, "createUploadSession")(
        serverUrl,
        accessToken,
        targetDirectoryPath,
        fileName,
        length,
        lastModifiedMillis,
        idempotencyKey,
    )

    override suspend fun uploadSession(serverUrl: String, accessToken: String, uploadId: String): ApiResult<UploadSession> {
        uploadSessionReads += uploadId
        return requireHandler(onUploadSession, "uploadSession")(serverUrl, accessToken, uploadId)
    }

    override suspend fun sendUploadChunk(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        offset: Long,
        chunkLength: Long,
        source: InputStream,
        onInFlight: ((Long) -> Unit)?,
    ): UploadChunkResult = requireHandler(onSendUploadChunk, "sendUploadChunk")(
        serverUrl,
        accessToken,
        uploadId,
        offset,
        chunkLength,
        source,
        onInFlight,
    )

    override suspend fun commitUpload(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        contentHash: String?,
    ): ApiResult<Unit> {
        committedUploadIds += uploadId
        return requireHandler(onCommitUpload, "commitUpload")(serverUrl, accessToken, uploadId, contentHash)
    }

    override suspend fun abortUpload(serverUrl: String, accessToken: String, uploadId: String): ApiResult<Unit> {
        abortedUploadIds += uploadId
        return requireHandler(onAbortUpload, "abortUpload")(serverUrl, accessToken, uploadId)
    }

    override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> =
        requireHandler(onPerformance, "performanceSnapshot")(serverUrl, accessToken)
    override suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
    ): ApiResult<ProcessPage> = requireHandler(onProcesses, "queryProcesses")(serverUrl, accessToken, page, pageSize, filter)

    override suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, force: Boolean): ApiResult<Unit> {
        killCalls += pid to force
        return requireHandler(onKill, "killProcess")(serverUrl, accessToken, pid, force)
    }

    override suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        sink: DownloadSink,
        onProgress: ((Long, Long?) -> Unit)?,
    ): ApiResult<Long> = requireHandler(onDownload, "download")(serverUrl, accessToken, path, sink, onProgress)

    override suspend fun thumbnail(
        serverUrl: String,
        accessToken: String,
        path: String,
        maxEdge: Int,
    ): ApiResult<ByteArray> = requireHandler(onThumbnail, "thumbnail")(serverUrl, accessToken, path, maxEdge)

    private fun <T> requireHandler(handler: T?, name: String): T =
        handler ?: error("FakeGateway.$name was called but no handler was configured.")
}

/** A successful login result with the given token values. */
fun loginSession(
    accessToken: String = "access-1",
    refreshToken: String = "refresh-1",
    userName: String = "nana",
    workspaceName: String = "studio",
    capabilities: Set<String> = emptySet(),
): LoginSession = LoginSession(
    userName = userName,
    workspaceName = workspaceName,
    server = ServerDescriptor(platform = "linux", capabilities = capabilities),
    tokens = AuthTokens(
        accessToken = accessToken,
        refreshToken = refreshToken,
        accessTokenExpiresAtMillis = null,
        refreshTokenExpiresAtMillis = null,
    ),
)
