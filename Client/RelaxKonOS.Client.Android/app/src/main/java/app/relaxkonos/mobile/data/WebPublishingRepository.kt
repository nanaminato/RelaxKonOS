package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.WebsitePublishRequest
import app.relaxkonos.mobile.core.net.WebsitePublicationOperation
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Session-scoped facade for AD05 diagnostics and confirmed publication intent. */
class WebPublishingRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val elevations: ElevationRepository,
    private val operationIndex: OperationIndex? = null,
) {
    private val reads = Mutex()

    suspend fun servers(owner: SessionState.Active) = read(owner) { url, token -> gateway.webServers(url, token) }
    suspend fun status(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerStatus(url, token, id) }
    suspend fun configTest(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerConfigTest(url, token, id) }
    suspend fun sites(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerSites(url, token, id) }
    suspend fun certificates(owner: SessionState.Active) = read(owner) { url, token -> gateway.certificates(url, token) }
    suspend fun history(owner: SessionState.Active, applicationId: String) =
        read(owner) { url, token -> gateway.websitePublicationHistory(url, token, applicationId) }
    suspend fun operation(owner: SessionState.Active, operationId: String) =
        read(owner) { url, token -> gateway.websitePublication(url, token, operationId) }

    suspend fun publish(request: WebsitePublishRequest, provider: ElevationAnswerProvider): ApiResult<WebsitePublicationOperation> {
        require(request.confirmed) { "Website publication must be confirmed." }
        val owner = session.state.value as? SessionState.Active
        val idempotencyKey = UUID.randomUUID().toString()
        val result = elevations.withElevation("nginxConfigurationWrite", request.webServerId, provider) { url, token ->
            gateway.publishWebsite(url, token, request, idempotencyKey)
        }
        if (result is ApiResult.Success) {
            if (owner != null && session.state.value === owner) runCatching {
                operationIndex?.record(owner, OperationDomain.Website, result.value.applicationId, result.value.operationId)
            }
        }
        return result
    }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() { if (session.state.value !== owner) throw CancellationException("Web publishing session changed") }
        verifyOwner()
        val result = session.authenticated { url, token -> verifyOwner(); call(url, token) }
        verifyOwner()
        val operation = (result as? ApiResult.Success)?.value as? WebsitePublicationOperation
        if (operation != null) runCatching {
            operationIndex?.record(owner, OperationDomain.Website, operation.applicationId, operation.operationId)
        }
        result
    }
}
