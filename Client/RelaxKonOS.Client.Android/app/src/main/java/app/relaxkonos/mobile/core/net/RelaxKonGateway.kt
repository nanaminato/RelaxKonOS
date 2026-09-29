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
    suspend fun alerts(serverUrl: String, accessToken: String, cursor: String?): ApiResult<OperationalAlertPage> =
        ApiResult.Transport("Operational alerts unavailable.")
    suspend fun alertDetail(serverUrl: String, accessToken: String, id: String): ApiResult<OperationalAlertDetail> =
        ApiResult.Transport("Operational alert unavailable.")
    suspend fun acknowledgeAlert(serverUrl: String, accessToken: String, id: String): ApiResult<OperationalAlert> =
        ApiResult.Transport("Operational alert acknowledgement unavailable.")
    suspend fun scriptTasks(serverUrl: String, accessToken: String): ApiResult<ScriptTasksResult> = ApiResult.Transport("Scripts unavailable.")
    suspend fun scriptTask(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> = ApiResult.Transport("Scripts unavailable.")
    suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> = ApiResult.Transport("Scripts unavailable.")
    suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> = ApiResult.Transport("Scripts unavailable.")
    suspend fun guardianStatus(serverUrl: String, accessToken: String): ApiResult<GuardianStatus> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianWorkloads(serverUrl: String, accessToken: String): ApiResult<List<GuardianWorkload>> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianDefinition(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianDefinition?> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianLogs(serverUrl: String, accessToken: String, id: String): ApiResult<List<GuardianLog>> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianSave(serverUrl: String, accessToken: String, definition: GuardianDefinition, approval: GuardianApproval?): ApiResult<GuardianOperation> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianAction(serverUrl: String, accessToken: String, id: String, action: String): ApiResult<GuardianOperation> = ApiResult.Transport("Guardian unavailable.")
    suspend fun guardianDelete(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianOperation> = ApiResult.Transport("Guardian unavailable.")
    suspend fun gitRepositories(serverUrl: String, accessToken: String): ApiResult<List<GitRepository>> = ApiResult.Transport("Git unavailable.")
    suspend fun gitRegisterRepository(serverUrl: String, accessToken: String, name: String, path: String): ApiResult<GitRepository> = ApiResult.Transport("Git unavailable.")
    suspend fun gitBranches(serverUrl: String, accessToken: String, id: String): ApiResult<List<GitBranch>> = ApiResult.Transport("Git unavailable.")
    suspend fun gitStatus(serverUrl: String, accessToken: String, id: String): ApiResult<GitStatus> = ApiResult.Transport("Git unavailable.")
    suspend fun gitTextFile(serverUrl: String, accessToken: String, id: String, path: String): ApiResult<GitTextFile> = ApiResult.Transport("Git editing unavailable.")
    suspend fun gitSaveTextFile(serverUrl: String, accessToken: String, id: String, file: GitTextFile, content: String): ApiResult<GitTextFile> = ApiResult.Transport("Git editing unavailable.")
    suspend fun gitCommit(serverUrl: String, accessToken: String, id: String, path: String, message: String): ApiResult<GitOperation> = ApiResult.Transport("Git unavailable.")
    suspend fun gitPush(serverUrl: String, accessToken: String, id: String): ApiResult<GitOperation> = ApiResult.Transport("Git unavailable.")
    suspend fun gitBuildCredentials(serverUrl: String, accessToken: String): ApiResult<List<GitBuildCredential>> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildSetCredential(serverUrl: String, accessToken: String, name: String, token: String): ApiResult<GitBuildCredential> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildResolve(serverUrl: String, accessToken: String, url: String, reference: String, credentialId: String?): ApiResult<GitBuildResolved> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildRefs(serverUrl: String, accessToken: String, url: String, credentialId: String?): ApiResult<List<GitBuildRef>> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuilds(serverUrl: String, accessToken: String): ApiResult<List<GitBuildOperation>> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildStart(serverUrl: String, accessToken: String, request: GitBuildRequest, key: String): ApiResult<GitBuildOperation> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildGet(serverUrl: String, accessToken: String, id: String): ApiResult<GitBuildOperation> = ApiResult.Transport("Git builds unavailable.")
    suspend fun gitBuildCancel(serverUrl: String, accessToken: String, id: String): ApiResult<GitBuildOperation> = ApiResult.Transport("Git builds unavailable.")
    suspend fun deployGitBuild(serverUrl: String, accessToken: String, applicationId: String, build: GitBuildOperation, key: String): ApiResult<DeploymentOperation> = ApiResult.Transport("Git deployment unavailable.")
    suspend fun webServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> = ApiResult.Transport("Web servers are unavailable.")
    suspend fun webServerStatus(serverUrl: String, accessToken: String, instanceId: String): ApiResult<WebServerStatus> = ApiResult.Transport("Web server status is unavailable.")
    /** A server-side syntax check: it does not write, reload, or start the web server. */
    suspend fun webServerConfigTest(serverUrl: String, accessToken: String, instanceId: String): ApiResult<WebServerConfigTest> = ApiResult.Transport("Web server configuration diagnostics are unavailable.")
    suspend fun webServerSites(serverUrl: String, accessToken: String, instanceId: String): ApiResult<List<WebServerSite>> = ApiResult.Transport("Web server sites are unavailable.")
    suspend fun certificates(serverUrl: String, accessToken: String): ApiResult<List<ManagedCertificate>> = ApiResult.Transport("Certificates are unavailable.")
    suspend fun publishWebsite(serverUrl: String, accessToken: String, request: WebsitePublishRequest, idempotencyKey: String): ApiResult<WebsitePublicationOperation> = ApiResult.Transport("Website publishing is unavailable.")
    suspend fun websitePublicationHistory(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<WebsitePublicationOperation>> = ApiResult.Transport("Website publishing history is unavailable.")
    suspend fun websitePublication(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebsitePublicationOperation> = ApiResult.Transport("Website publishing operation is unavailable.")

    suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerContainers(serverUrl: String, accessToken: String): ApiResult<List<DockerContainer>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerImages(serverUrl: String, accessToken: String): ApiResult<List<DockerImage>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerNetworks(serverUrl: String, accessToken: String): ApiResult<List<DockerNetwork>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerVolumes(serverUrl: String, accessToken: String): ApiResult<List<DockerVolume>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerVolumeDetails(serverUrl: String, accessToken: String, name: String): ApiResult<DockerVolumeDetails> = ApiResult.Transport("Docker is unavailable.")
    /** Releases a volume's data. The server refuses while a container still references it. */
    suspend fun dockerDeleteVolume(serverUrl: String, accessToken: String, name: String, confirmed: Boolean): ApiResult<DockerOperation> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerStacks(serverUrl: String, accessToken: String): ApiResult<List<DockerStack>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerStackServices(serverUrl: String, accessToken: String, name: String): ApiResult<List<DockerStackService>> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerContainerLogs(serverUrl: String, accessToken: String, id: String, tail: Int): ApiResult<DockerLogs> = ApiResult.Transport("Docker is unavailable.")
    suspend fun dockerContainerAction(serverUrl: String, accessToken: String, id: String, action: String, confirmed: Boolean): ApiResult<DockerOperation> = ApiResult.Transport("Docker is unavailable.")
    /** Parses a Compose definition without applying it. Nothing on the host changes. */
    suspend fun dockerStackPreview(serverUrl: String, accessToken: String, name: String, composeYaml: String): ApiResult<DockerStackPreview> = ApiResult.Transport("Docker Compose is unavailable.")
    /** Submits a deployment, or returns the operation already bound to this idempotency key. */
    suspend fun dockerStackDeploy(serverUrl: String, accessToken: String, name: String, composeYaml: String, definitionVersion: String, idempotencyKey: String): ApiResult<DockerStackOperation> = ApiResult.Transport("Docker Compose is unavailable.")
    suspend fun dockerStackAction(serverUrl: String, accessToken: String, name: String, action: String, confirmed: Boolean, idempotencyKey: String): ApiResult<DockerStackOperation> = ApiResult.Transport("Docker Compose is unavailable.")
    suspend fun dockerStackOperations(serverUrl: String, accessToken: String, name: String, limit: Int): ApiResult<List<DockerStackOperation>> = ApiResult.Transport("Docker Compose is unavailable.")
    suspend fun dockerStackOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<DockerStackOperation> = ApiResult.Transport("Docker Compose is unavailable.")
    suspend fun dockerStackOperationDiagnostics(serverUrl: String, accessToken: String, operationId: String): ApiResult<DockerStackOperationDiagnostics> = ApiResult.Transport("Docker Compose is unavailable.")
    suspend fun dockerStackOperationCancel(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<DockerStackOperation> = ApiResult.Transport("Docker Compose is unavailable.")

    suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>>
    suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot>
    suspend fun deploymentRuntime(serverUrl: String, accessToken: String): ApiResult<DeploymentRuntime>

    suspend fun deploymentTemplates(serverUrl: String, accessToken: String): ApiResult<List<DeploymentTemplate>> =
        ApiResult.Transport("Application deployment templates are unavailable.")

    suspend fun applicationCatalog(serverUrl: String, accessToken: String): ApiResult<List<CatalogTemplate>> =
        ApiResult.Transport("Application catalogue is unavailable.")

    suspend fun installCatalogApplication(serverUrl: String, accessToken: String, template: CatalogTemplate, name: String,
        fields: List<CatalogFieldValue>, idempotencyKey: String): ApiResult<DeploymentOperation> =
        ApiResult.Transport("Application catalogue install is unavailable.")

    /** Streams one selected archive directly into deployment-owned staging; callers must not buffer it. */
    suspend fun uploadDeploymentArchive(
        serverUrl: String,
        accessToken: String,
        fileName: String,
        contentLength: Long?,
        open: () -> InputStream,
    ): ApiResult<DeploymentArchive> = ApiResult.Transport("Application deployment archive upload is unavailable.")

    /** Stages an archive already readable by the authenticated server user. */
    suspend fun stageServerDeploymentArchive(
        serverUrl: String,
        accessToken: String,
        path: String,
    ): ApiResult<DeploymentArchive> = ApiResult.Transport("Server deployment archive selection is unavailable.")

    /** Creates an image deployment definition; publishing it is the separate call below. */
    suspend fun createImageDeployment(
        serverUrl: String,
        accessToken: String,
        definition: ImageDeploymentDefinition,
        idempotencyKey: String,
    ): ApiResult<DeploymentApplication> = ApiResult.Transport("Image deployment creation is unavailable.")

    suspend fun deploymentImageTags(serverUrl: String, accessToken: String, repository: String): ApiResult<DeploymentImageTags> =
        ApiResult.Transport("Image tag lookup is unavailable.")

    suspend fun deploymentOperationDiagnostics(serverUrl: String, accessToken: String, operationId: String): ApiResult<DeploymentOperationDiagnostics> =
        ApiResult.Transport("Deployment operation diagnostics are unavailable.")
    suspend fun deploymentOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<DeploymentOperation> =
        ApiResult.Transport("Deployment operation is unavailable.")

    suspend fun createArchiveDeployment(
        serverUrl: String,
        accessToken: String,
        definition: ArchiveDeploymentDefinition,
        idempotencyKey: String,
    ): ApiResult<DeploymentApplication> = ApiResult.Transport("Archive deployment creation is unavailable.")

    suspend fun deployArchive(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        archiveReferenceId: String,
        definition: ArchiveDeploymentDefinition,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = ApiResult.Transport("Archive deployment is unavailable.")

    /** Queues a confirmed image deployment and returns its durable operation record. */
    suspend fun deployImage(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        imageReference: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = ApiResult.Transport("Image deployment is unavailable.")

    /** Queues a confirmed switch back to an immutable, server-known revision. */
    suspend fun rollbackDeployment(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        revisionId: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = ApiResult.Transport("Application rollback is unavailable.")

    /** Removes the application while explicitly retaining managed volumes. */
    suspend fun deleteDeployment(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = ApiResult.Transport("Application removal is unavailable.")

    suspend fun deploymentLifecycle(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        action: DeploymentLifecycleAction,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = ApiResult.Transport("Application lifecycle actions are unavailable.")

    suspend fun deploymentLogs(serverUrl: String, accessToken: String, applicationId: String, tail: Int = 200): ApiResult<DeploymentLog> =
        ApiResult.Transport("Application logs are unavailable.")

    suspend fun cancelDeploymentOperation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        ApiResult.Transport("Deployment cancellation is unavailable.")

    /**
     * The operating system class the server at [serverUrl] runs on, asked with no credential at all.
     *
     * Anonymous because of *when* it is needed: the connection list is opened before any session
     * exists, and a password is exactly what the user may not have for that server. The route answers
     * this one fact and nothing else (`RelaxKonOS.Protocol.ServerApiRoutes.HostOperatingSystem`).
     */
    suspend fun hostOperatingSystem(serverUrl: String): ApiResult<HostOperatingSystemKind> =
        ApiResult.Transport("Host operating system lookup is unavailable.")

    suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession>

    /** Enrols this Android Keystore public key with a one-time invitation issued by a Windows owner device. */
    suspend fun acceptOwnerDeviceInvitation(
        serverUrl: String,
        invitationToken: String,
        deviceName: String,
        publicKeySpki: String,
    ): ApiResult<OwnerDeviceEnrollment> = ApiResult.Transport("Owner-device pairing is unavailable.")

    /** Obtains the one-use nonce for a previously enrolled device key. */
    suspend fun ownerDeviceChallenge(serverUrl: String, deviceId: String): ApiResult<OwnerDeviceChallenge> =
        ApiResult.Transport("Owner-device sign-in is unavailable.")

    /** Exchanges a signature over an owner-device nonce for the regular RelaxKonOS session. */
    suspend fun signInWithOwnerDevice(
        serverUrl: String,
        challengeId: String,
        deviceId: String,
        signature: String,
    ): ApiResult<LoginSession> = ApiResult.Transport("Owner-device sign-in is unavailable.")

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
