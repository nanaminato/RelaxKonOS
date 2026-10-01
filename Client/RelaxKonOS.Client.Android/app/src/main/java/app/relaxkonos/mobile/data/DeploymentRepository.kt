package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Reads never request elevation or infer management permission from execution eligibility. */
class DeploymentRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val operationIndex: OperationIndex? = null,
) {
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

    suspend fun operation(owner: SessionState.Active, operationId: String): ApiResult<DeploymentOperation> = read(owner) { url, token ->
        gateway.deploymentOperation(url, token, operationId)
    }

    suspend fun catalog(owner: SessionState.Active): ApiResult<List<CatalogTemplate>> = read(owner) { url, token ->
        gateway.applicationCatalog(url, token)
    }

    suspend fun installCatalog(owner: SessionState.Active, template: CatalogTemplate, name: String, hostPort: Int, fields: List<CatalogFieldValue>, key: String): ApiResult<DeploymentOperation> =
        read(owner) { url, token -> gateway.installCatalogApplication(url, token, template, name, hostPort, fields, key) }

    /** Each authentication retry repeats the baseline check; a lost response is never replayed. */
    suspend fun saveDefinition(owner: SessionState.Active, baseline: DeploymentApplication, definition: DeploymentDefinitionUpdate, key: String): DeploymentDefinitionSave {
        var dispatched = false
        val result = read(owner) { url, token ->
            when (val before = gateway.deploymentSnapshot(url, token, baseline.id)) {
                is ApiResult.Success -> {
                    verifyOwner(owner)
                    val snapshot = before.value
                    if (snapshot.application.id != baseline.id) return@read ApiResult.Transport("Unexpected deployment identity.")
                    if (snapshot.application.updatedAt != definition.expectedUpdatedAt || definition.expectedUpdatedAt != baseline.updatedAt)
                        return@read ApiResult.Problem(409, "application-deployment.definition_conflict", null)
                    if (snapshot.activeOperation != null) return@read ApiResult.Problem(409, "application-deployment.resource_conflict", null)
                }
                is ApiResult.Problem -> return@read before
                is ApiResult.Transport -> return@read before
            }
            verifyOwner(owner)
            dispatched = true
            when (val saved = gateway.updateDeploymentDefinition(url, token, baseline.id, definition, key)) {
                is ApiResult.Success -> {
                    verifyOwner(owner)
                    val receipt = saved.value
                    if (!definition.matchesReceipt(receipt, baseline)) return@read ApiResult.Transport("Deployment definition receipt did not match.")
                    when (val after = gateway.deploymentSnapshot(url, token, baseline.id)) {
                        is ApiResult.Success -> {
                            verifyOwner(owner)
                            if (!receipt.sameStoredDefinition(after.value.application)) ApiResult.Transport("Deployment definition readback did not match.")
                            else ApiResult.Success(after.value.application)
                        }
                        else -> ApiResult.Transport("Deployment definition readback is unavailable.")
                    }
                }
                is ApiResult.Problem -> saved
                is ApiResult.Transport -> saved
            }
        }
        return DeploymentDefinitionSave(result, dispatched && (result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500))
    }

    @Volatile private var revisionOwner: SessionState.Active? = null
    private val revisionWrites = Mutex()
    private val uncertainRevisions = java.util.Collections.synchronizedSet(mutableSetOf<String>())

    fun hasUncertainRevision(owner: SessionState.Active, id: String): Boolean = revisionOwner === owner && id in uncertainRevisions

    /** Clearing an unknown result adopts current facts; it never asserts that the lost request succeeded. */
    suspend fun reconcileRevision(owner: SessionState.Active, id: String): ApiResult<DeploymentSnapshot> = revisionWrites.withLock {
        val result = snapshot(owner, id)
        if (result is ApiResult.Success && result.value.application.id == id && result.value.activeOperation == null && revisionOwner === owner) {
            uncertainRevisions.remove(id)
        }
        result
    }

    suspend fun previewCatalogUpdate(owner: SessionState.Active, id: String, templateVersion: String): ApiResult<CatalogApplicationUpdatePreview> =
        read(owner) { url, token -> gateway.previewCatalogUpdate(url, token, id, templateVersion) }

    suspend fun updateCatalog(owner: SessionState.Active, baseline: DeploymentApplication,
        preview: CatalogApplicationUpdatePreview, key: String): DeploymentRevisionSubmission {
        if (preview.applicationId != baseline.id || preview.expectedUpdatedAt != baseline.updatedAt || preview.target.id != baseline.catalogTemplateId ||
            preview.currentTemplateVersion != baseline.catalogTemplateVersion || preview.blockers.isNotEmpty() || !preview.target.trusted ||
            preview.target.withdrawn || preview.target.schemaVersion != "1" || !preview.target.requiredCapabilities.all(owner.capabilities::contains))
            return DeploymentRevisionSubmission(ApiResult.Problem(409, "application-deployment.definition_conflict", null), false)
        return submitRevision(owner, baseline) { url, token, dispatch ->
            when (val current = gateway.previewCatalogUpdate(url, token, baseline.id, preview.target.version)) {
                is ApiResult.Success -> {
                    verifyOwner(owner)
                    if (current.value != preview) ApiResult.Problem(409, "application-deployment.definition_conflict", null)
                    else { dispatch(); gateway.updateCatalogApplication(url, token, preview, key) }
                }
                is ApiResult.Problem -> current
                is ApiResult.Transport -> current
            }
        }
    }

    suspend fun deployRevision(owner: SessionState.Active, baseline: DeploymentApplication,
        source: DeploymentRevisionSource, key: String): DeploymentRevisionSubmission {
        if (!source.validFor(baseline.sourceKind)) return DeploymentRevisionSubmission(ApiResult.Problem(400, "application-deployment.invalid_request", null), false)
        return submitRevision(owner, baseline) { url, token, dispatch -> dispatch(); gateway.deployRevision(url, token, baseline.id, source, baseline.updatedAt, key) }
    }

    private suspend fun submitRevision(owner: SessionState.Active, baseline: DeploymentApplication,
        call: suspend (String, String, () -> Unit) -> ApiResult<DeploymentOperation>): DeploymentRevisionSubmission = revisionWrites.withLock {
        verifyOwner(owner)
        if (revisionOwner !== owner) { revisionOwner = owner; uncertainRevisions.clear() }
        if (baseline.id in uncertainRevisions) return@withLock DeploymentRevisionSubmission(ApiResult.Problem(409, "application-deployment.revision_unknown", null), true)
        var dispatched = false
        var resolved = false
        try {
            val result = read(owner) { url, token ->
                when (val before = gateway.deploymentSnapshot(url, token, baseline.id)) {
                    is ApiResult.Success -> {
                        verifyOwner(owner)
                        if (!baseline.sameStoredDefinition(before.value.application) || baseline.currentRevisionNumber != before.value.application.currentRevisionNumber)
                            return@read ApiResult.Problem(409, "application-deployment.definition_conflict", null)
                        if (before.value.activeOperation != null) return@read ApiResult.Problem(409, "application-deployment.resource_conflict", null)
                    }
                    is ApiResult.Problem -> return@read before
                    is ApiResult.Transport -> return@read before
                }
                verifyOwner(owner)
                when (val accepted = call(url, token) { dispatched = true }) {
                    is ApiResult.Success -> if (accepted.value.applicationId == baseline.id && accepted.value.kind == "deploy") accepted
                        else ApiResult.Transport("Unexpected revision receipt.")
                    else -> accepted
                }
            }
            val unknown = dispatched && (result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500)
            if (unknown) uncertainRevisions.add(baseline.id)
            resolved = true
            DeploymentRevisionSubmission(result, unknown)
        } finally {
            if (dispatched && !resolved && session.state.value === owner) uncertainRevisions.add(baseline.id)
        }
    }

    suspend fun createImageDefinition(owner: SessionState.Active, definition: ImageDeploymentDefinition, key: String): ApiResult<DeploymentApplication> =
        read(owner) { url, token -> gateway.createImageDeployment(url, token, definition, key) }

    suspend fun deployGitBuild(owner: SessionState.Active, applicationId: String, build: GitBuildOperation, key: String): ApiResult<DeploymentOperation> {
        return when (val current = snapshot(owner, applicationId)) {
            is ApiResult.Success -> if (current.value.application.id != applicationId || current.value.application.sourceKind != "image") ApiResult.Problem(409, "application-deployment.definition_conflict", null)
                else submitRevision(owner, current.value.application) { url, token, dispatch ->
                    dispatch(); gateway.deployGitBuild(url, token, applicationId, build, current.value.application.updatedAt, key)
                }.result
            is ApiResult.Problem -> current
            is ApiResult.Transport -> current
        }
    }

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
            is ApiResult.Success -> { verifyOwner(owner); gateway.deployImage(url, token, created.value.id, imageReference, created.value.updatedAt, deploymentKey) }
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
            is ApiResult.Success -> { verifyOwner(owner); gateway.deployArchive(url, token, created.value.id, archiveReferenceId, definition, created.value.updatedAt, deploymentKey) }
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
        val operation = (result as? ApiResult.Success)?.value as? DeploymentOperation
        if (operation != null) runCatching {
            operationIndex?.record(owner, OperationDomain.Deployment, operation.applicationId, operation.operationId)
        }
        result
    }

    private fun verifyOwner(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Deployment session changed")
    }
}

data class DeploymentDefinitionSave(val result: ApiResult<DeploymentApplication>, val mayHaveSaved: Boolean)

/** Compare every writable field and the server-owned identity; secret bodies never enter readback. */
internal fun DeploymentDefinitionUpdate.matchesReceipt(actual: DeploymentApplication, baseline: DeploymentApplication): Boolean =
    actual.id == baseline.id && actual.sourceKind == baseline.sourceKind && actual.catalogTemplateId == baseline.catalogTemplateId &&
        actual.catalogTemplateVersion == baseline.catalogTemplateVersion && actual.updatedAt != expectedUpdatedAt &&
        name == actual.name && workloadKind == actual.workloadKind && readinessLevel == actual.readinessLevel &&
        healthCheckPath == actual.healthCheckPath && containerPort == actual.containerPort && hostPort == actual.hostPort &&
        bindAddress == actual.bindAddress && limits == actual.limits && volumes == actual.volumes && siteId == actual.siteId &&
        configuration.size == actual.configuration.size && configuration.all { expected ->
            actual.configuration.singleOrNull { it.name == expected.name }?.let { observed ->
                observed.isSecret == expected.isSecret && if (!expected.isSecret) observed.value == expected.value && observed.secretVersion == null
                else observed.value == null && if (expected.value == null) observed.secretVersion == expected.secretVersion
                else observed.secretVersion != null && observed.secretVersion > (expected.secretVersion ?: 0)
            } == true
        }

internal fun DeploymentApplication.sameStoredDefinition(other: DeploymentApplication): Boolean =
    id == other.id && updatedAt == other.updatedAt && name == other.name && sourceKind == other.sourceKind &&
        workloadKind == other.workloadKind && readinessLevel == other.readinessLevel && healthCheckPath == other.healthCheckPath &&
        containerPort == other.containerPort && hostPort == other.hostPort && bindAddress == other.bindAddress && limits == other.limits &&
        volumes == other.volumes && configuration == other.configuration && siteId == other.siteId &&
        catalogTemplateId == other.catalogTemplateId && catalogTemplateVersion == other.catalogTemplateVersion

data class DeploymentRevisionSubmission(val result: ApiResult<DeploymentOperation>, val mayHaveQueued: Boolean)
