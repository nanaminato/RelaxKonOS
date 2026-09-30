package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import java.security.MessageDigest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Synchronous site mutations have no operation IDs or server idempotency support. Recover via factual readback. */
class WebSiteRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val journal: WebSiteMutationJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active): List<PendingSiteMutation> { verify(owner); return journal.pending(owner) }
    suspend fun sites(owner: SessionState.Active, serverId: String): ApiResult<List<WebServerSite>> {
        verify(owner)
        val result = session.authenticated { url, token -> verify(owner); gateway.webServerSites(url, token, serverId) }
        verify(owner)
        if (result is ApiResult.Success && (result.value.any { it.serverId != serverId } || result.value.map { it.id }.distinct().size != result.value.size)) return ApiResult.Transport(null)
        return result
    }
    suspend fun save(owner: SessionState.Active, serverId: String, request: WebServerSiteRequest, provider: ElevationAnswerProvider): ApiResult<WebServerSite> = mutations.withLock {
        verifyManage(owner)
        val pending = journal.begin(PendingSiteMutation(owner.serviceId, owner.userName, serverId, request.id, false,
            digest(request.body().toByteArray()), request.expectedUpdatedAt))
        if (pending.attempted) {
            when (val facts = sites(owner, serverId)) {
                is ApiResult.Success -> if (facts.value.firstOrNull { it.id == request.id }?.updatedAt != request.expectedUpdatedAt)
                    return@withLock ApiResult.Problem(409, "webserver.site_changed", null)
                is ApiResult.Problem -> return@withLock facts
                is ApiResult.Transport -> return@withLock facts
            }
        }
        journal.attempted(pending)
        val result = elevations.withElevation("nginxConfigurationWrite", serverId, provider) { url, token ->
            verify(owner); gateway.saveWebServerSite(url, token, serverId, request)
        }
        verify(owner)
        if (result is ApiResult.Success) {
            if (result.value.serverId != serverId || result.value.id != request.id || (request.expectedUpdatedAt != null && result.value.updatedAt == request.expectedUpdatedAt)) return@withLock ApiResult.Transport(null)
            journal.complete(pending)
        } else if (result is ApiResult.Problem && !pending.attempted && isPreflightRefusal(result)) journal.complete(pending)
        result
    }
    suspend fun delete(owner: SessionState.Active, site: WebServerSite, provider: ElevationAnswerProvider): ApiResult<Unit> = mutations.withLock {
        verifyManage(owner)
        val pending = journal.begin(PendingSiteMutation(owner.serviceId, owner.userName, site.serverId, site.id, true,
            digest("delete:${site.id}:${site.updatedAt}".toByteArray()), site.updatedAt))
        if (pending.attempted) {
            when (val facts = sites(owner, site.serverId)) {
                is ApiResult.Success -> if (facts.value.firstOrNull { it.id == site.id }?.updatedAt != site.updatedAt)
                    return@withLock ApiResult.Problem(409, "webserver.site_changed", null)
                is ApiResult.Problem -> return@withLock facts
                is ApiResult.Transport -> return@withLock facts
            }
        }
        journal.attempted(pending)
        val result = elevations.withElevation("nginxConfigurationWrite", site.serverId, provider) { url, token ->
            verify(owner); gateway.deleteWebServerSite(url, token, site.serverId, site.id, site.updatedAt)
        }
        verify(owner)
        if (result is ApiResult.Success || (result is ApiResult.Problem && !pending.attempted && isPreflightRefusal(result))) journal.complete(pending)
        result
    }
    /** Explicitly accepts a fresh observation; this makes no claim about which request caused it. */
    suspend fun acceptFacts(owner: SessionState.Active, pending: PendingSiteMutation): ApiResult<List<WebServerSite>> = mutations.withLock {
        verify(owner); require(pending in journal.pending(owner))
        val result = sites(owner, pending.serverId)
        if (result is ApiResult.Success) journal.complete(pending)
        result
    }
    private fun isPreflightRefusal(result: ApiResult.Problem): Boolean = result.status in setOf(401, 403, 409) ||
        result.code in setOf("webserver.site_name_invalid", "webserver.site_server_name_required", "webserver.site_port_invalid",
            "webserver.site_server_name_invalid", "webserver.site_certificate_required", "webserver.site_certificate_not_usable", "webserver.site_certificate_domain_mismatch", "webserver.site_certificate_file_invalid",
            "webserver.site_root_invalid", "webserver.site_route_path_invalid", "webserver.site_upstream_invalid",
            "webserver.site_content_required", "webserver.site_https_redirect_invalid")
    private fun digest(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    private fun verifyManage(owner: SessionState.Active) { verify(owner); require(owner.privilegedOperations && ServerCapabilities.WEB_SERVER in owner.capabilities) }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Web site session changed") }
}
