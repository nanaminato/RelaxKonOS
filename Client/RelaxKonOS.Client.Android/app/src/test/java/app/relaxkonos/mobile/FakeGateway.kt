package app.relaxkonos.mobile

import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.DeploymentLog
import app.relaxkonos.mobile.core.net.DeploymentOperation
import app.relaxkonos.mobile.core.net.DeploymentSnapshot
import app.relaxkonos.mobile.core.net.DeploymentRuntime
import app.relaxkonos.mobile.core.net.DeploymentTemplate
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.DownloadSink
import app.relaxkonos.mobile.core.net.ElevationGrant
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
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
    var onManagedFrps: suspend () -> ApiResult<ManagedFrps> = { error("Unexpected frps read") }
    var onManagedFrpsEditing: suspend () -> ApiResult<ManagedFrpsEditing> = { error("Unexpected frps secret read") }
    var onSaveManagedFrps: suspend (ManagedFrpsRequest) -> ApiResult<ManagedFrps> = { error("Unexpected frps write") }
    var onStartManagedFrps: suspend () -> ApiResult<TunnelResult> = { error("Unexpected frps start") }
    var onStopManagedFrps: suspend () -> ApiResult<TunnelResult> = { error("Unexpected frps stop") }
    var onManagedFrpsLogs: suspend () -> ApiResult<List<TunnelLog>> = { error("Unexpected frps logs") }
    var onManagedFrpsAudit: suspend () -> ApiResult<List<TunnelAudit>> = { error("Unexpected frps audit") }
    override suspend fun managedFrps(serverUrl: String, accessToken: String) = onManagedFrps()
    override suspend fun managedFrpsEditing(serverUrl: String, accessToken: String) = onManagedFrpsEditing()
    override suspend fun saveManagedFrps(serverUrl: String, accessToken: String, request: ManagedFrpsRequest) = onSaveManagedFrps(request)
    override suspend fun startManagedFrps(serverUrl: String, accessToken: String) = onStartManagedFrps()
    override suspend fun stopManagedFrps(serverUrl: String, accessToken: String) = onStopManagedFrps()
    override suspend fun managedFrpsLogs(serverUrl: String, accessToken: String) = onManagedFrpsLogs()
    override suspend fun managedFrpsAudit(serverUrl: String, accessToken: String) = onManagedFrpsAudit()
    var onTunnelProfiles: suspend () -> ApiResult<List<TunnelProfile>> = { error("Unexpected profiles read") }
    var onTunnelDefinitions: suspend () -> ApiResult<List<TunnelDefinition>> = { error("Unexpected definitions read") }
    var onTunnelRuntime: suspend () -> ApiResult<TunnelRuntime> = { error("Unexpected runtime read") }
    var onTunnelDownload: suspend (String) -> ApiResult<TunnelRuntimeDownload> = { error("Unexpected download read") }
    var onTunnelDetect: suspend (String) -> ApiResult<TunnelRuntime> = { error("Unexpected external detection") }
    var onSaveTunnelProfile: suspend (String?, TunnelProfileRequest) -> ApiResult<TunnelProfile> = { _, _ -> error("Unexpected profile write") }
    var onDeleteTunnelProfile: suspend (String) -> ApiResult<Unit> = { error("Unexpected profile delete") }
    var onTunnelToken: suspend (String, String) -> ApiResult<Unit> = { _, _ -> error("Unexpected token write") }
    var onSaveTunnelDefinition: suspend (String?, TunnelDefinitionRequest) -> ApiResult<TunnelDefinition> = { _, _ -> error("Unexpected definition write") }
    var onDeleteTunnelDefinition: suspend (String) -> ApiResult<Unit> = { error("Unexpected definition delete") }
    var onApplyTunnelProfile: suspend (String) -> ApiResult<TunnelResult> = { error("Unexpected profile apply") }
    var onStopTunnelProfile: suspend (String) -> ApiResult<TunnelResult> = { error("Unexpected profile stop") }
    var onTunnelLogs: suspend (String) -> ApiResult<List<TunnelLog>> = { error("Unexpected logs read") }
    override suspend fun tunnelProfiles(serverUrl: String, accessToken: String) = onTunnelProfiles()
    override suspend fun tunnelDefinitions(serverUrl: String, accessToken: String) = onTunnelDefinitions()
    override suspend fun tunnelRuntime(serverUrl: String, accessToken: String) = onTunnelRuntime()
    override suspend fun tunnelRuntimeDownload(serverUrl: String, accessToken: String, version: String) = onTunnelDownload(version)
    override suspend fun detectTunnelRuntime(serverUrl: String, accessToken: String, path: String) = onTunnelDetect(path)
    override suspend fun saveTunnelProfile(serverUrl: String, accessToken: String, id: String?, request: TunnelProfileRequest) = onSaveTunnelProfile(id, request)
    override suspend fun deleteTunnelProfile(serverUrl: String, accessToken: String, id: String) = onDeleteTunnelProfile(id)
    override suspend fun setTunnelToken(serverUrl: String, accessToken: String, id: String, secret: String) = onTunnelToken(id, secret)
    override suspend fun saveTunnelDefinition(serverUrl: String, accessToken: String, id: String?, request: TunnelDefinitionRequest) = onSaveTunnelDefinition(id, request)
    override suspend fun deleteTunnelDefinition(serverUrl: String, accessToken: String, id: String) = onDeleteTunnelDefinition(id)
    override suspend fun applyTunnelProfile(serverUrl: String, accessToken: String, id: String) = onApplyTunnelProfile(id)
    override suspend fun stopTunnelProfile(serverUrl: String, accessToken: String, id: String) = onStopTunnelProfile(id)
    override suspend fun tunnelLogs(serverUrl: String, accessToken: String, id: String) = onTunnelLogs(id)
    var onKestrelDeployment: suspend (String) -> ApiResult<KestrelCertificateDeployment> = { ApiResult.Transport(null) }
    override suspend fun kestrelCertificateDeployment(serverUrl: String, accessToken: String, id: String) = onKestrelDeployment(id)
    var onCertificates: suspend () -> ApiResult<List<ManagedCertificate>> = { ApiResult.Success(emptyList()) }
    var onCertificate: suspend (String) -> ApiResult<ManagedCertificate> = { ApiResult.Transport(null) }
    var onCertificatePreflight: suspend (List<String>, CertificateChallenge) -> ApiResult<CertificatePreflight> = { _, _ -> ApiResult.Transport(null) }
    var onCertificateMutation: suspend (CertificateAction, String?, JsonBody, String) -> ApiResult<CertificateOperation> = { _, _, _, _ -> ApiResult.Transport(null) }
    var onCertificateOperation: suspend (String) -> ApiResult<CertificateOperation> = { ApiResult.Transport(null) }
    var onCertificateCancel: suspend (String) -> ApiResult<CertificateOperation> = { ApiResult.Transport(null) }
    override suspend fun certificates(serverUrl: String, accessToken: String) = onCertificates()
    override suspend fun certificate(serverUrl: String, accessToken: String, id: String) = onCertificate(id)
    override suspend fun certificatePreflight(serverUrl: String, accessToken: String, domains: List<String>, challenge: CertificateChallenge) = onCertificatePreflight(domains, challenge)
    override suspend fun certificateMutation(serverUrl: String, accessToken: String, action: CertificateAction, id: String?, body: JsonBody, idempotencyKey: String) = onCertificateMutation(action, id, body, idempotencyKey)
    override suspend fun certificateOperation(serverUrl: String, accessToken: String, id: String) = onCertificateOperation(id)
    override suspend fun cancelCertificateOperation(serverUrl: String, accessToken: String, id: String) = onCertificateCancel(id)
    var onWebSites: (suspend (String) -> ApiResult<List<WebServerSite>>)? = null
    override suspend fun webServerSites(serverUrl: String, accessToken: String, instanceId: String): ApiResult<List<WebServerSite>> = requireNotNull(onWebSites)(instanceId)
    var onSaveWebSite: (suspend (String, WebServerSiteRequest) -> ApiResult<WebServerSite>)? = null
    var onDeleteWebSite: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    override suspend fun saveWebServerSite(serverUrl: String, accessToken: String, instanceId: String, request: WebServerSiteRequest): ApiResult<WebServerSite> = requireNotNull(onSaveWebSite)(instanceId, request)
    override suspend fun deleteWebServerSite(serverUrl: String, accessToken: String, instanceId: String, siteId: String, expectedUpdatedAt: String): ApiResult<Unit> = requireNotNull(onDeleteWebSite)(instanceId, siteId, expectedUpdatedAt)
    var onWebDiscover: () -> ApiResult<List<WebServer>> = { ApiResult.Success(emptyList()) }
    var onWebLifecycle: (String, WebServerAction, String) -> ApiResult<WebServerOperation> = { _, _, _ -> ApiResult.Transport(null) }
    var onWebOperation: (String) -> ApiResult<WebServerOperation> = { ApiResult.Transport(null) }
    override suspend fun discoverWebServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> = onWebDiscover()
    override suspend fun webServerCandidates(serverUrl: String, accessToken: String): ApiResult<List<WebServerCandidate>> = ApiResult.Success(emptyList())
    override suspend fun webServerInstallCatalog(serverUrl: String, accessToken: String): ApiResult<WebServerInstallCatalog> = ApiResult.Success(WebServerInstallCatalog(null, null, emptyList(), ""))
    override suspend fun integrateWebServer(serverUrl: String, accessToken: String, candidateId: String, confirmed: Boolean, idempotencyKey: String): ApiResult<WebServerOperation> = ApiResult.Transport(null)
    override suspend fun webServerLifecycle(serverUrl: String, accessToken: String, instanceId: String, action: WebServerAction, idempotencyKey: String): ApiResult<WebServerOperation> = onWebLifecycle(instanceId, action, idempotencyKey)
    override suspend fun webServerOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebServerOperation> = onWebOperation(operationId)
    override suspend fun cancelWebServerOperation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<WebServerOperation> = ApiResult.Transport(null)

    var onOutboundProxyStatus: (suspend () -> ApiResult<OutboundProxyStatus>)? = null
    var onSaveOutboundProxy: (suspend (OutboundProxySettings, Boolean) -> ApiResult<OutboundProxyStatus>)? = null
    var onClearOutboundProxy: (suspend () -> ApiResult<OutboundProxyStatus>)? = null
    override suspend fun outboundProxyStatus(serverUrl: String, accessToken: String) = requireNotNull(onOutboundProxyStatus)()
    override suspend fun saveOutboundProxy(serverUrl: String, accessToken: String, settings: OutboundProxySettings, confirmed: Boolean) =
        requireNotNull(onSaveOutboundProxy)(settings, confirmed)
    override suspend fun clearOutboundProxy(serverUrl: String, accessToken: String) = requireNotNull(onClearOutboundProxy)()

    var onStartInstallation: (suspend (InstallationKind, InstallationRequest, String) -> ApiResult<InstallationOperation>)? = null
    var onInstallation: (suspend (String) -> ApiResult<InstallationOperation>)? = null
    var onActiveInstallation: (suspend (InstallationService) -> ApiResult<InstallationOperation?>)? = null
    var onCancelInstallation: (suspend (String, String) -> ApiResult<InstallationOperation>)? = null
    var onInstallationFileReference: (suspend (InstallationService, String) -> ApiResult<InstallationFileReference>)? = null
    var onInstallationUpload: (suspend (InstallationService, String, Long?, () -> InputStream) -> ApiResult<InstallationFileReference>)? = null
    override suspend fun startInstallation(serverUrl: String, accessToken: String, kind: InstallationKind,
        request: InstallationRequest, idempotencyKey: String) = requireNotNull(onStartInstallation)(kind, request, idempotencyKey)
    override suspend fun installation(serverUrl: String, accessToken: String, operationId: String) = requireNotNull(onInstallation)(operationId)
    override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService) = requireNotNull(onActiveInstallation)(service)
    override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String,
        idempotencyKey: String) = requireNotNull(onCancelInstallation)(operationId, idempotencyKey)
    override suspend fun installationFileReference(serverUrl: String, accessToken: String, service: InstallationService,
        path: String) = requireNotNull(onInstallationFileReference)(service, path)
    override suspend fun uploadInstallationPackage(serverUrl: String, accessToken: String, service: InstallationService,
        fileName: String, length: Long?, open: () -> InputStream, onProgress: ((Long) -> Unit)?) =
        requireNotNull(onInstallationUpload)(service, fileName, length, open)

    var onDeploymentApplications: (suspend (String, String) -> ApiResult<List<DeploymentApplication>>)? = null
    var onDeploymentSnapshot: (suspend (String, String, String) -> ApiResult<DeploymentSnapshot>)? = null
    var onDeploymentRuntime: (suspend (String, String) -> ApiResult<DeploymentRuntime>)? = null
    var onDeploymentTemplates: (suspend (String, String) -> ApiResult<List<DeploymentTemplate>>)? = null
    var onDeploymentLogs: (suspend (String, String, String, Int) -> ApiResult<DeploymentLog>)? = null
    var onRollbackDeployment: (suspend (String, String, String, String, String) -> ApiResult<DeploymentOperation>)? = null

    override suspend fun deploymentApplications(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentApplications)(serverUrl, accessToken)
    override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String) =
        requireNotNull(onDeploymentSnapshot)(serverUrl, accessToken, applicationId)
    override suspend fun deploymentRuntime(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentRuntime)(serverUrl, accessToken)
    override suspend fun deploymentTemplates(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentTemplates)(serverUrl, accessToken)
    override suspend fun deploymentLogs(serverUrl: String, accessToken: String, applicationId: String, tail: Int) =
        requireNotNull(onDeploymentLogs)(serverUrl, accessToken, applicationId, tail)
    override suspend fun rollbackDeployment(serverUrl: String, accessToken: String, applicationId: String, revisionId: String, idempotencyKey: String) =
        requireNotNull(onRollbackDeployment)(serverUrl, accessToken, applicationId, revisionId, idempotencyKey)

    var onLogin: (suspend (String, String, CharArray) -> ApiResult<LoginSession>)? = null
    var onHostOperatingSystem: (suspend (String) -> ApiResult<HostOperatingSystemKind>)? = null
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

    /** Addresses asked for their host operating system, so a test can prove who was *not* asked. */
    val hostOperatingSystemLookups = mutableListOf<String>()

    override suspend fun hostOperatingSystem(serverUrl: String): ApiResult<HostOperatingSystemKind> {
        hostOperatingSystemLookups += serverUrl
        return requireHandler(onHostOperatingSystem, "hostOperatingSystem")(serverUrl)
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
