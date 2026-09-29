package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentOperation
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.WebsitePublicationOperation
import kotlinx.coroutines.CancellationException

enum class OperationCheck { Verified, Unavailable, Missing }

data class ObservedOperation(
    val reference: OperationReference,
    val check: OperationCheck,
    val target: String,
    val state: String? = null,
    val stage: String? = null,
    val progress: Int? = null,
    val problemCode: String? = null,
    val cancellable: Boolean = false,
    val checkedAtMillis: Long? = null,
)

data class OperationCenterSnapshot(val items: List<ObservedOperation>, val incomplete: Boolean)

/** Reads each domain's own durable record. Discovery also recovers operations not yet in the local index. */
class OperationCenter(
    private val session: AuthSession,
    private val index: OperationIndex,
    private val deployments: DeploymentRepository,
    private val websites: WebPublishingRepository,
) {
    suspend fun refresh(owner: SessionState.Active): OperationCenterSnapshot {
        verify(owner)
        val discovered = linkedMapOf<Pair<OperationDomain, String>, ObservedOperation>()
        var incomplete = false
        val applications = if (ServerCapabilities.APPLICATION_DEPLOYMENTS in owner.capabilities) deployments.applications(owner) else null
        if (applications != null && applications !is ApiResult.Success) incomplete = true
        if (applications is ApiResult.Success) {
            if (applications.value.size > 100) incomplete = true
            applications.value.take(100).forEach { application ->
                verify(owner)
                val snapshot = deployments.snapshot(owner, application.id)
                if (snapshot !is ApiResult.Success) incomplete = true
                if (snapshot is ApiResult.Success) (snapshot.value.operations + listOfNotNull(snapshot.value.activeOperation))
                    .distinctBy { it.operationId }.forEach operationLoop@ { operation ->
                    if (index.isHidden(owner, OperationDomain.Deployment, operation.operationId)) return@operationLoop
                    runCatching { index.record(owner, OperationDomain.Deployment, application.id, operation.operationId) }
                    val observed = fromDeployment(owner, operation, application.name)
                    discovered[OperationDomain.Deployment to operation.operationId] = observed
                }
                if (ServerCapabilities.WEB_SERVER in owner.capabilities) {
                    val history = websites.history(owner, application.id)
                    if (history !is ApiResult.Success) incomplete = true
                    if (history is ApiResult.Success) history.value.forEach publicationLoop@ { operation ->
                        if (index.isHidden(owner, OperationDomain.Website, operation.operationId)) return@publicationLoop
                        runCatching { index.record(owner, OperationDomain.Website, application.id, operation.operationId) }
                        discovered[OperationDomain.Website to operation.operationId] = fromWebsite(owner, operation, application.name)
                    }
                }
            }
        }
        index.forOwner(owner).forEach { reference ->
            verify(owner)
            val key = reference.domain to reference.operationId
            if (key !in discovered) discovered[key] = query(owner, reference)
        }
        verify(owner)
        return OperationCenterSnapshot(discovered.values.sortedByDescending { it.reference.seenAtMillis }, incomplete)
    }

    suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation): ApiResult<List<String>> {
        verify(owner)
        if (item.reference.domain != OperationDomain.Deployment) return ApiResult.Success(emptyList())
        return when (val result = deployments.operationDiagnostics(owner, item.reference.operationId)) {
            is ApiResult.Success -> ApiResult.Success(result.value.lines)
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    suspend fun cancel(owner: SessionState.Active, item: ObservedOperation): ApiResult<DeploymentOperation> {
        verify(owner)
        if (item.reference.domain != OperationDomain.Deployment || !item.cancellable) {
            return ApiResult.Transport("Cancellation is unavailable for this operation.")
        }
        val key = java.util.UUID.nameUUIDFromBytes(
            "cancel:${owner.serviceId}:${owner.userName}:${item.reference.operationId}".toByteArray(Charsets.UTF_8)).toString()
        return deployments.cancel(owner, item.reference.operationId, key)
    }

    fun hide(owner: SessionState.Active, item: ObservedOperation) = index.hide(owner, item.reference)

    private suspend fun query(owner: SessionState.Active, reference: OperationReference): ObservedOperation =
        when (reference.domain) {
            OperationDomain.Deployment -> when (val result = deployments.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.applicationId == reference.resourceId)
                    fromDeployment(owner, result.value, reference.resourceId) else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Website -> when (val result = websites.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.applicationId == reference.resourceId) fromWebsite(owner, result.value, result.value.domain)
                    else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
        }

    private fun fromDeployment(owner: SessionState.Active, operation: DeploymentOperation, target: String): ObservedOperation =
        verified(owner, OperationDomain.Deployment, operation.applicationId, operation.operationId,
            target, operation.state, operation.stage, operation.progress,
            operation.problemCode ?: operation.recoveryProblemCode, operation.cancellable)

    private fun fromWebsite(owner: SessionState.Active, operation: WebsitePublicationOperation, target: String): ObservedOperation =
        verified(owner, OperationDomain.Website, operation.applicationId, operation.operationId,
            target, operation.state, operation.stage, null,
            operation.problemCode.takeIf { it.isNotBlank() } ?: operation.recoveryProblemCode)

    private fun verified(owner: SessionState.Active, domain: OperationDomain, resourceId: String, operationId: String,
        target: String, state: String, stage: String, progress: Int?, problemCode: String?, cancellable: Boolean = false): ObservedOperation {
        val checkedAt = System.currentTimeMillis()
        runCatching { index.markVerified(owner, domain, operationId, checkedAt) }
        return ObservedOperation(reference(owner, domain, resourceId, operationId), OperationCheck.Verified,
            target, state, stage, progress, problemCode, cancellable, checkedAt)
    }

    private fun reference(owner: SessionState.Active, domain: OperationDomain, resourceId: String, operationId: String) =
        index.forOwner(owner).firstOrNull { it.domain == domain && it.operationId == operationId }
            ?: OperationReference(owner.serviceId, owner.userName, domain, resourceId, operationId, System.currentTimeMillis())

    private fun unknown(reference: OperationReference, check: OperationCheck) =
        ObservedOperation(reference, check, reference.resourceId,
            checkedAtMillis = reference.lastVerifiedAtMillis)

    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Operation center session changed")
    }
}
