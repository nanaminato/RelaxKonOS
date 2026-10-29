package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant
import java.util.UUID

/** A challenge can only be consumed by an explicit browser handoff in the current authenticated session. */
class TailscaleLoginRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val references: TailscaleOperationReferences,
    private val now: () -> Instant = Instant::now) {
    private val changes = Mutex()
    fun records(owner: SessionState.Active): List<TailscaleReference> { verify(owner); return references.read(owner) }
    suspend fun prepare(owner: SessionState.Active, provider: ElevationAnswerProvider): ApiResult<TailscaleLoginOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && records(owner).none { it.phase != TailscaleReferencePhase.Known })
        val id = UUID.randomUUID().toString()
        val result = elevated(owner,provider) { url,token -> gateway.prepareTailscaleLogin(url,token,id) }
        if (result is ApiResult.Success) {
            check(result.value.operationId == id && result.value.state == TailscaleLoginState.Prepared && result.value.closure == null && result.value.expiresAt > now())
            references.remember(owner,id)
        }
        result
    }
    suspend fun execute(owner: SessionState.Active, plan: TailscaleLoginOperation, confirmed: Boolean,
        provider: ElevationAnswerProvider): ApiResult<TailscaleLoginOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && confirmed && plan.state == TailscaleLoginState.Prepared && plan.expiresAt > now())
        require(records(owner).any { it.operationId == plan.operationId } && records(owner).none { it.phase != TailscaleReferencePhase.Known })
        references.mark(owner,plan.operationId,TailscaleReferencePhase.Executing)
        currentCoroutineContext().ensureActive(); verify(owner)
        val result = elevated(owner,provider) { url,token ->
            if (plan.expiresAt <= now()) ApiResult.Problem(409,"tailscale.plan_expired",null)
            else gateway.executeTailscaleLogin(url,token,plan.operationId)
        }
        if (result is ApiResult.Success) {
            check(result.value.expectedRevision == plan.expectedRevision && result.value.expiresAt == plan.expiresAt)
            accept(owner,plan.operationId,result.value)
        }
        result
    }
    suspend fun read(owner: SessionState.Active, id: String): ApiResult<TailscaleLoginOperation> = changes.withLock {
        require(records(owner).any { it.operationId == id })
        val result = session.authenticated { url,token -> verify(owner); gateway.tailscaleLoginOperation(url,token,id) }
        currentCoroutineContext().ensureActive(); verify(owner)
        if (result is ApiResult.Success) accept(owner,id,result.value)
        result
    }
    suspend fun handoff(owner: SessionState.Active, operation: TailscaleLoginOperation, provider: ElevationAnswerProvider,
        open: suspend (String) -> Boolean): ApiResult<Boolean> = changes.withLock {
        verify(owner)
        require(operation.state == TailscaleLoginState.Issued && operation.closure == null && operation.expiresAt > now())
        require(records(owner).any { it.operationId == operation.operationId && it.phase != TailscaleReferencePhase.Closing })
        when (val result = elevated(owner,provider) { url,token -> gateway.tailscaleLoginChallenge(url,token,operation.operationId) }) {
            is ApiResult.Success -> {
                val challenge = result.value
                check(challenge.operationId == operation.operationId && challenge.expiresAt == operation.expiresAt
                    && challenge.expiresAt > now() && TailscaleLoginWire.officialUrl(challenge.authenticationUrl))
                currentCoroutineContext().ensureActive(); verify(owner)
                ApiResult.Success(open(challenge.authenticationUrl))
            }
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }
    suspend fun recovery(owner: SessionState.Active, operation: TailscaleLoginOperation,
        provider: ElevationAnswerProvider): ApiResult<TailscaleLoginRecovery> = changes.withLock {
        verify(owner); require(operation.closure == null && operation.state in recoverable && records(owner).any { it.operationId == operation.operationId })
        elevated(owner,provider) { url,token -> gateway.tailscaleLoginRecovery(url,token,operation.operationId) }.also { result ->
            if (result is ApiResult.Success) {
                check(result.value.operationId == operation.operationId)
                check(!result.value.canClose || result.value.observation != null && result.value.expiresAt != null && result.value.expiresAt > now() && result.value.closure == null)
            }
        }
    }
    suspend fun close(owner: SessionState.Active, preview: TailscaleLoginRecovery, confirmed: Boolean,
        provider: ElevationAnswerProvider): ApiResult<TailscaleLoginOperation> = changes.withLock {
        verify(owner); require(owner.privilegedOperations && confirmed && preview.canClose && preview.observation != null && preview.expiresAt != null && preview.expiresAt > now())
        require(records(owner).any { it.operationId == preview.operationId })
        references.mark(owner,preview.operationId,TailscaleReferencePhase.Closing)
        currentCoroutineContext().ensureActive(); verify(owner)
        val result = elevated(owner,provider) { url,token ->
            if (preview.expiresAt <= now()) ApiResult.Problem(409,"tailscale.plan_expired",null)
            else gateway.closeTailscaleLogin(url,token,preview.operationId,preview.observation.revision)
        }
        if (result is ApiResult.Success) {
            check(result.value.closure?.observation?.revision == preview.observation.revision)
            accept(owner,preview.operationId,result.value)
        }
        result
    }
    private fun accept(owner: SessionState.Active, id: String, operation: TailscaleLoginOperation) {
        verify(owner); check(operation.operationId == id)
        operation.closure?.let {
            check(operation.state in recoverable && it.intent.operationId == id && it.intent.expectedRevision == operation.expectedRevision && it.intent.expiresAt == operation.expiresAt)
        }
        val reference = records(owner).single { it.operationId == id }
        if (operation.state == TailscaleLoginState.Prepared) return
        val resolved = operation.closure != null || reference.phase != TailscaleReferencePhase.Closing
            && operation.state in setOf(TailscaleLoginState.Prepared,TailscaleLoginState.Completed,TailscaleLoginState.Rejected)
        if (resolved) references.mark(owner,id,TailscaleReferencePhase.Known)
        else if (reference.phase == TailscaleReferencePhase.Known) references.mark(owner,id,TailscaleReferencePhase.Executing)
    }
    private suspend fun <T> elevated(owner: SessionState.Active, provider: ElevationAnswerProvider, action: suspend (String,String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return elevations.withElevation("hostNetworkChange","host/tailscale",provider) { url,token ->
            verify(owner)
            action(url,token).let { if (it is ApiResult.Problem && it.code == "tailscale.elevation_required") ApiResult.Problem(403,ProblemCodes.ELEVATION_REQUIRED,null) else it }
        }.also { currentCoroutineContext().ensureActive(); verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Tailscale login session changed")
        require(ServerCapabilities.TAILSCALE in owner.capabilities)
    }
    companion object { val recoverable = setOf(TailscaleLoginState.Issued,TailscaleLoginState.Unknown,TailscaleLoginState.ApprovalRequired) }
}
