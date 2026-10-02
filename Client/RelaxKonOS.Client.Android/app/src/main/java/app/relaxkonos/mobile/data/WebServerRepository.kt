package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class WebServerRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val index: OperationIndex,
    private val journal: WebServerRequestJournal) {
    private val mutations = Mutex()
    suspend fun discover(owner: SessionState.Active) = read(owner) { url, token -> gateway.discoverWebServers(url, token) }
    suspend fun candidates(owner: SessionState.Active) = read(owner) { url, token -> gateway.webServerCandidates(url, token) }
    suspend fun catalog(owner: SessionState.Active) = read(owner) { url, token -> gateway.webServerInstallCatalog(url, token) }
    suspend fun download(owner: SessionState.Active, version: String) = read(owner) { url, token -> gateway.webServerInstallDownload(url, token, version) }
    fun pending(owner: SessionState.Active): List<PendingWebServerRequest> { verify(owner); return journal.pending(owner) }

    suspend fun integrate(owner: SessionState.Active, candidate: WebServerCandidate, provider: ElevationAnswerProvider) =
        submit(owner, journal.begin(owner.also(::verifyManagement), candidate.id, "integrate"), provider)
    suspend fun lifecycle(owner: SessionState.Active, server: WebServer, action: WebServerAction, provider: ElevationAnswerProvider): ApiResult<WebServerOperation> {
        verifyManagement(owner)
        require(action.supported(server))
        return submit(owner, journal.begin(owner, server.id, action.route), provider)
    }

    /** Explicit replay keeps the original key. Never replay after a transport failure automatically. */
    suspend fun resume(owner: SessionState.Active, pending: PendingWebServerRequest, provider: ElevationAnswerProvider): ApiResult<WebServerOperation> {
        verify(owner)
        require(pending in journal.pending(owner))
        if (pending.operationId == null) {
            // Refresh host facts first; discovery alone is not proof that the original operation succeeded.
            val facts = discover(owner)
            if (facts !is ApiResult.Success) return when (facts) {
                is ApiResult.Problem -> facts
                is ApiResult.Transport -> facts
                else -> error("Unreachable")
            }
        }
        return submit(owner, pending, provider)
    }

    private suspend fun submit(owner: SessionState.Active, pending: PendingWebServerRequest,
        provider: ElevationAnswerProvider): ApiResult<WebServerOperation> = mutations.withLock {
        verify(owner)
        require(owner.privilegedOperations && ServerCapabilities.WEB_SERVER in owner.capabilities)
        val knownId = pending.operationId
        if (knownId != null) return@withLock operation(owner, knownId).also { if (it is ApiResult.Success) journal.complete(pending) }
        journal.update(pending.copy(attempted = true))
        val action = if (pending.action == "integrate") null else WebServerAction.entries.single { it.route == pending.action }
        val result = elevations.withElevation(action?.elevation ?: "nginxConfigurationWrite", pending.target, provider) { url, token ->
            verify(owner)
            if (action == null) gateway.integrateWebServer(url, token, pending.target, true, pending.key)
            else gateway.webServerLifecycle(url, token, pending.target, action, pending.key)
        }
        verify(owner)
        if (result is ApiResult.Success) {
            if (result.value.instanceId != pending.target || result.value.kind != (action?.kind ?: "integrate"))
                return@withLock ApiResult.Transport(null)
            journal.update(pending.copy(operationId = result.value.operationId, attempted = true))
            record(owner, result.value)
            journal.complete(pending)
        }
        if (result is ApiResult.Problem && !pending.attempted && result.status in setOf(400, 401, 403)) journal.complete(pending)
        result
    }
    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<WebServerOperation> =
        read(owner) { url, token -> gateway.webServerOperation(url, token, id) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != InstallationRoutes.canonicalId(id)) return@let ApiResult.Transport(null)
                record(owner, result.value)
                journal.pending(owner).filter { it.operationId == result.value.operationId }.forEach(journal::complete)
            }
            result
        }
    suspend fun recoverById(owner: SessionState.Active, id: String): ApiResult<WebServerOperation> {
        val result = operation(owner, id)
        if (result is ApiResult.Success) { verify(owner); index.reveal(owner, OperationDomain.WebServer, result.value.operationId) }
        return result
    }
    suspend fun cancel(owner: SessionState.Active, id: String): ApiResult<WebServerOperation> = mutations.withLock {
        val canonical = InstallationRoutes.canonicalId(id)
        val key = UUID.nameUUIDFromBytes("webserver-cancel:${owner.serviceId}:${owner.userName}:$canonical".toByteArray()).toString()
        read(owner) { url, token -> gateway.cancelWebServerOperation(url, token, canonical, key) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != canonical) return@withLock ApiResult.Transport(null)
                record(owner, result.value)
            }
            result
        }
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return session.authenticated { url, token -> verify(owner); call(url, token) }.also { verify(owner) }
    }
    private fun record(owner: SessionState.Active, operation: WebServerOperation) {
        verify(owner); index.record(owner, OperationDomain.WebServer, operation.instanceId, operation.operationId)
    }
    private fun verifyManagement(owner: SessionState.Active) {
        verify(owner)
        require(owner.privilegedOperations && ServerCapabilities.WEB_SERVER in owner.capabilities)
    }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Web-server session changed") }
}
