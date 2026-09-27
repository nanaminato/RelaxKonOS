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

    /** The archive travels as a stream into deployment-owned staging, then only its opaque reference reaches the operation. */
    suspend fun createAndDeployArchive(
        owner: SessionState.Active,
        definition: ArchiveDeploymentDefinition,
        archive: PickedDocument,
        definitionKey: String,
        deploymentKey: String,
    ): ApiResult<DeploymentOperation> = read(owner) { url, token ->
        when (val created = gateway.createArchiveDeployment(url, token, definition, definitionKey)) {
            is ApiResult.Success -> when (val staged = gateway.uploadDeploymentArchive(url, token, archive.displayName, archive.length, archive.open)) {
                is ApiResult.Success -> gateway.deployArchive(url, token, created.value.id, staged.value.referenceId, definition, deploymentKey)
                is ApiResult.Problem -> staged
                is ApiResult.Transport -> staged
            }
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

    suspend fun logs(owner: SessionState.Active, applicationId: String): ApiResult<DeploymentLog> =
        read(owner) { url, token -> gateway.deploymentLogs(url, token, applicationId) }

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
