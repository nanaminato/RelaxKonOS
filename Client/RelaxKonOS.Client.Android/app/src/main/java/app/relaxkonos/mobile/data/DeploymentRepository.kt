package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Reads never request elevation or infer management permission from execution eligibility. */
class DeploymentRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    // A list refresh can overlap a detail refresh. Serialize their auth retries so both do not
    // try to rotate the same refresh token when the access token expires.
    private val reads = Mutex()
    suspend fun applications(owner: SessionState.Active): ApiResult<List<DeploymentApplication>> = read(owner) { url, token ->
        gateway.deploymentApplications(url, token)
    }

    suspend fun snapshot(owner: SessionState.Active, id: String): ApiResult<DeploymentSnapshot> = read(owner) { url, token ->
        gateway.deploymentSnapshot(url, token, id)
    }

    suspend fun runtime(owner: SessionState.Active): ApiResult<DeploymentRuntime> = read(owner) { url, token ->
        gateway.deploymentRuntime(url, token)
    }

    suspend fun templates(owner: SessionState.Active): ApiResult<List<DeploymentTemplate>> = read(owner) { url, token ->
        gateway.deploymentTemplates(url, token)
    }

    suspend fun imageTags(owner: SessionState.Active, repository: String): ApiResult<DeploymentImageTags> = read(owner) { url, token ->
        gateway.deploymentImageTags(url, token, repository)
    }

    suspend fun operationDiagnostics(owner: SessionState.Active, operationId: String): ApiResult<DeploymentOperationDiagnostics> = read(owner) { url, token ->
        gateway.deploymentOperationDiagnostics(url, token, operationId)
    }

    suspend fun catalog(owner: SessionState.Active): ApiResult<List<CatalogTemplate>> = read(owner) { url, token ->
        gateway.applicationCatalog(url, token)
    }

    suspend fun installCatalog(owner: SessionState.Active, template: CatalogTemplate, name: String, fields: List<CatalogFieldValue>, key: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.installCatalogApplication(url, token, template, name, fields, key) }

    suspend fun createImageDefinition(owner: SessionState.Active, definition: ImageDeploymentDefinition, key: String): ApiResult<DeploymentApplication> =
        read(owner) { url, token -> gateway.createImageDeployment(url, token, definition, key) }

    suspend fun deployGitBuild(owner: SessionState.Active, applicationId: String, build: GitBuildOperation, key: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.deployGitBuild(url, token, applicationId, build, key) }

    suspend fun createArchiveDefinition(owner: SessionState.Active, definition: ArchiveDeploymentDefinition, key: String): ApiResult<DeploymentApplication> =
        read(owner) { url, token -> gateway.createArchiveDeployment(url, token, definition, key) }

    /**
     * Creates a definition and then queues its first revision. The two server actions intentionally
     * retain distinct keys: retrying after a lost response returns the same definition or operation
     * rather than allocating a duplicate application.
     */
    suspend fun createAndDeployImage(
        owner: SessionState.Active,
        definition: ImageDeploymentDefinition,
        imageReference: String,
        definitionKey: String,
        deploymentKey: String,
    ): ApiResult<DeploymentOperation> = read(owner) { url, token ->
        when (val created = gateway.createImageDeployment(url, token, definition, definitionKey)) {
            is ApiResult.Success -> gateway.deployImage(url, token, created.value.id, imageReference, deploymentKey)
            is ApiResult.Problem -> created
            is ApiResult.Transport -> created
        }
    }

    suspend fun stageArchive(owner: SessionState.Active, archive: PickedDocument): ApiResult<DeploymentArchive> =
        read(owner) { url, token -> gateway.uploadDeploymentArchive(url, token, archive.displayName, archive.length, archive.open) }

    suspend fun stageServerArchive(owner: SessionState.Active, path: String): ApiResult<DeploymentArchive> =
        read(owner) { url, token -> gateway.stageServerDeploymentArchive(url, token, path) }

    /** The entry step has already staged the archive; only its opaque reference reaches deployment. */
    suspend fun createAndDeployArchive(
        owner: SessionState.Active,
        definition: ArchiveDeploymentDefinition,
        archiveReferenceId: String,
        definitionKey: String,
        deploymentKey: String,
    ): ApiResult<DeploymentOperation> = read(owner) { url, token ->
        when (val created = gateway.createArchiveDeployment(url, token, definition, definitionKey)) {
            is ApiResult.Success -> gateway.deployArchive(url, token, created.value.id, archiveReferenceId, definition, deploymentKey)
            is ApiResult.Problem -> created
            is ApiResult.Transport -> created
        }
    }

    suspend fun rollback(owner: SessionState.Active, applicationId: String, revisionId: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.rollbackDeployment(url, token, applicationId, revisionId, idempotencyKey) }

    suspend fun delete(owner: SessionState.Active, applicationId: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.deleteDeployment(url, token, applicationId, idempotencyKey) }

    suspend fun lifecycle(owner: SessionState.Active, applicationId: String, action: DeploymentLifecycleAction, idempotencyKey: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.deploymentLifecycle(url, token, applicationId, action, idempotencyKey) }

    suspend fun logs(owner: SessionState.Active, applicationId: String, tail: Int): ApiResult<DeploymentLog> =
        read(owner) { url, token -> gateway.deploymentLogs(url, token, applicationId, tail) }

    suspend fun cancel(owner: SessionState.Active, operationId: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.cancelDeploymentOperation(url, token, operationId, idempotencyKey) }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() {
            if (session.state.value !== owner) throw CancellationException("Deployment session changed")
        }
        verifyOwner()
        val result = session.authenticated { url, token ->
            verifyOwner() // Also guards the retry after a token refresh.
            call(url, token)
        }
        verifyOwner()
        result
    }
}
