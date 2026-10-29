package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant
import java.util.UUID

class TailscaleControlRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val references: TailscaleOperationReferences,
    private val now: () -> Instant = Instant::now) {
    private val changes = Mutex()
    fun records(owner: SessionState.Active) = references.read(owner.also(::verify))
    suspend fun control(owner: SessionState.Active, provider: ElevationAnswerProvider) = elevated(owner,provider,gateway::tailscaleControl)
    suspend fun prepare(owner: SessionState.Active, baseline: TailscaleControl, action: TailscaleAction,
        configuration: TailscaleConfiguration?, provider: ElevationAnswerProvider): ApiResult<TailscaleOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && baseline.canControl)
        require(records(owner).none { it.phase != TailscaleReferencePhase.Known })
        require(action != TailscaleAction.Connect || baseline.authenticated && !baseline.connected)
        require(action != TailscaleAction.Disconnect || baseline.connected)
        require(action != TailscaleAction.Configure || configuration != baseline.configuration)
        val request = TailscalePlanRequest(UUID.randomUUID().toString(),baseline.revision,action,configuration)
        request.body()
        val result = elevated(owner,provider) { url,token -> gateway.prepareTailscale(url,token,request) }
        if (result is ApiResult.Success) {
            val plan = result.value
            check(plan.operationId == request.operationId && plan.action == action && plan.expectedRevision == baseline.revision
                && plan.configuration == configuration && plan.state == TailscaleOperationState.Prepared && plan.closure == null && plan.expiresAt > now())
            references.remember(owner,plan.operationId)
        }
        result
    }
    suspend fun execute(owner: SessionState.Active, plan: TailscaleOperation, confirmed: Boolean,
        provider: ElevationAnswerProvider): ApiResult<TailscaleOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && confirmed && plan.state == TailscaleOperationState.Prepared && plan.expiresAt > now())
        require(records(owner).any { it.operationId == plan.operationId } && records(owner).none { it.phase != TailscaleReferencePhase.Known })
        references.mark(owner,plan.operationId,TailscaleReferencePhase.Executing)
        currentCoroutineContext().ensureActive(); verify(owner)
        val result = elevated(owner,provider) { url,token ->
            verify(owner)
            if (plan.expiresAt <= now()) ApiResult.Problem(409,"tailscale.plan_expired",null)
            else gateway.executeTailscale(url,token,plan.operationId)
        }
        if (result is ApiResult.Success) {
            check(result.value.action == plan.action && result.value.expectedRevision == plan.expectedRevision && result.value.configuration == plan.configuration
                && result.value.expiresAt == plan.expiresAt)
            accept(owner,plan.operationId,result.value)
        }
        result
    }
    suspend fun read(owner: SessionState.Active, id: String): ApiResult<TailscaleOperation> = changes.withLock {
        require(records(owner).any { it.operationId == id })
        val result = call(owner) { url,token -> gateway.tailscaleOperation(url,token,id) }
        if (result is ApiResult.Success) accept(owner,id,result.value)
        result
    }
    suspend fun recovery(owner: SessionState.Active, id: String, provider: ElevationAnswerProvider): ApiResult<TailscaleRecovery> = changes.withLock {
        require(records(owner).any { it.operationId == id })
        elevated(owner,provider) { url,token -> gateway.tailscaleRecovery(url,token,id) }.also { result ->
            if (result is ApiResult.Success) {
                check(result.value.operationId == id)
                check(!result.value.canClose || result.value.observation != null && result.value.expiresAt != null && result.value.expiresAt > now())
            }
        }
    }
    suspend fun close(owner: SessionState.Active, preview: TailscaleRecovery, confirmed: Boolean,
        provider: ElevationAnswerProvider): ApiResult<TailscaleOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && confirmed && preview.canClose && preview.observation != null && preview.expiresAt != null && preview.expiresAt > now())
        require(records(owner).any { it.operationId == preview.operationId })
        references.mark(owner,preview.operationId,TailscaleReferencePhase.Closing)
        currentCoroutineContext().ensureActive(); verify(owner)
        val result = elevated(owner,provider) { url,token ->
            if (preview.expiresAt <= now()) ApiResult.Problem(409,"tailscale.plan_expired",null)
            else gateway.closeTailscale(url,token,preview.operationId,preview.observation.revision)
        }
        if (result is ApiResult.Success) {
            check(result.value.closure?.observation?.revision == preview.observation.revision)
            accept(owner,preview.operationId,result.value)
        }
        result
    }
    private fun accept(owner: SessionState.Active, id: String, operation: TailscaleOperation) {
        verify(owner); check(operation.operationId == id)
        operation.closure?.let { check(it.operationId == id && operation.state == TailscaleOperationState.Unknown) }
        val reference = records(owner).single { it.operationId == id }
        val resolved = if (reference.phase == TailscaleReferencePhase.Closing) operation.closure != null else
            operation.closure != null || operation.state in setOf(TailscaleOperationState.Applied,TailscaleOperationState.Rejected,TailscaleOperationState.Prepared)
        if (resolved) references.mark(owner,id,TailscaleReferencePhase.Known)
        else if (reference.phase == TailscaleReferencePhase.Known)
            references.mark(owner,id,TailscaleReferencePhase.Executing)
    }
    private suspend fun <T> elevated(owner: SessionState.Active, provider: ElevationAnswerProvider, action: suspend (String,String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return elevations.withElevation("hostNetworkChange","host/tailscale",provider) { url,token ->
            verify(owner)
            action(url,token).let { if (it is ApiResult.Problem && it.code == "tailscale.elevation_required") ApiResult.Problem(403,ProblemCodes.ELEVATION_REQUIRED,null) else it }
        }.also { currentCoroutineContext().ensureActive(); verify(owner) }
    }
    private suspend fun <T> call(owner: SessionState.Active, action: suspend (String,String) -> ApiResult<T>) =
        session.authenticated { url,token -> verify(owner); action(url,token) }.also { currentCoroutineContext().ensureActive(); verify(owner) }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Tailscale session changed")
        require(ServerCapabilities.TAILSCALE in owner.capabilities)
    }
}
