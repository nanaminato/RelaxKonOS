package app.relaxkonos.mobile

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.ElevationGrant
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.LoginSession
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerDescriptor
import java.io.File

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
    var onPerformance: (suspend (String, String) -> ApiResult<PerformanceSnapshot>)? = null
    var onProcesses: (suspend (String, String, Int, Int, String?) -> ApiResult<ProcessPage>)? = null
    var onKill: (suspend (String, String, Int, Boolean) -> ApiResult<Unit>)? = null
    var onDownload: (suspend (String, String, String, File) -> ApiResult<Long>)? = null

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

    override suspend fun download(serverUrl: String, accessToken: String, path: String, target: File): ApiResult<Long> =
        requireHandler(onDownload, "download")(serverUrl, accessToken, path, target)

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
