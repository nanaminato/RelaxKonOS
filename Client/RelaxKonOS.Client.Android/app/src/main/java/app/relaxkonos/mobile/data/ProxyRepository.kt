package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class ProxyRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val index: OperationIndex, private val journal: ProxyRequestJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active) = journal.pending(owner.also(::verify))
    suspend fun overview(owner: SessionState.Active) = read(owner, gateway::proxyOverview)
    suspend fun profiles(owner: SessionState.Active) = read(owner, gateway::proxyProfiles)
    suspend fun subscriptions(owner: SessionState.Active) = read(owner, gateway::proxySubscriptions)
    suspend fun groups(owner: SessionState.Active) = read(owner, gateway::proxyGroups)
    suspend fun routing(owner: SessionState.Active) = read(owner, gateway::proxyRouting)
    suspend fun downloadOptions(owner: SessionState.Active) = read(owner, gateway::proxyDownloadOptions)
    suspend fun download(owner: SessionState.Active, version: String) = read(owner) { u, t -> gateway.proxyDownload(u, t, version) }
    suspend fun settings(owner: SessionState.Active) = read(owner, gateway::proxySettings)
    suspend fun recovery(owner: SessionState.Active) = read(owner, gateway::proxyRecovery)
    suspend fun traffic(owner: SessionState.Active) = read(owner, gateway::proxyTraffic)
    suspend fun connections(owner: SessionState.Active) = read(owner, gateway::proxyConnections)
    suspend fun logs(owner: SessionState.Active) = read(owner, gateway::proxyLogs)
    suspend fun dns(owner: SessionState.Active) = read(owner, gateway::proxyDns)
    suspend fun geoData(owner: SessionState.Active) = read(owner, gateway::proxyGeoData)
    suspend fun saveSettings(owner: SessionState.Active, settings: ProxySettings) = mutate(owner, ProxyWrite.Settings, null) { u, t -> gateway.saveProxySettings(u, t, settings) }
    suspend fun configureGeoData(owner: SessionState.Active, path: String) = mutate(owner, ProxyWrite.GeoData, null) { u, t -> gateway.configureProxyGeoData(u, t, path) }
    suspend fun closeConnection(owner: SessionState.Active, id: String) = mutate(owner, ProxyWrite.Connection, null) { u, t -> gateway.closeProxyConnection(u, t, id) }
    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> {
        val canonical = InstallationRoutes.canonicalId(id)
        return read(owner) { u, t -> gateway.proxyOperation(u, t, canonical) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != canonical) return@let ApiResult.Transport(null)
                index.record(owner, OperationDomain.Proxy, result.value.kind, canonical)
            }; result
        }
    }
    suspend fun queue(owner: SessionState.Active, action: ProxyAction, target: String? = null) = mutations.withLock {
        manage(owner); val pending = journal.begin(owner, action, target?.let(InstallationRoutes::canonicalId)); submit(owner, pending)
    }
    suspend fun resume(owner: SessionState.Active, pending: PendingProxyRequest) = mutations.withLock {
        manage(owner); require(pending in journal.pending(owner) && pending.action != null)
        // Read current host state before explicit replay with the original key.
        if (pending.action != ProxyAction.EmergencyDisableTun) {
            val facts = overview(owner)
            if (facts !is ApiResult.Success) return@withLock when (facts) { is ApiResult.Problem -> facts; is ApiResult.Transport -> facts; else -> error("Unreachable") }
        }
        submit(owner, pending)
    }
    private suspend fun submit(owner: SessionState.Active, pending: PendingProxyRequest): ApiResult<ProxyOperation> {
        val action = requireNotNull(pending.action)
        val accepted = read(owner) { u, t -> gateway.proxyQueue(u, t, action, pending.target, pending.key) }
        return when (accepted) {
            is ApiResult.Success -> {
                // Save ID before dropping the request key; querying failure never loses the accepted task.
                index.record(owner, OperationDomain.Proxy, action.kind, accepted.value); journal.complete(pending)
                operation(owner, accepted.value).let { result ->
                    if (result is ApiResult.Success && result.value.kind != action.kind) ApiResult.Transport(null) else result
                }
            }
            is ApiResult.Problem -> accepted.also { if (it.status in setOf(400, 401, 403)) journal.complete(pending) }
            is ApiResult.Transport -> accepted
        }
    }
    suspend fun saveProfile(owner: SessionState.Active, id: String?, request: ProxyProfileRequest) = mutate(owner, ProxyWrite.Profile, id) { u, t -> gateway.saveProxyProfile(u, t, id, request) }
    suspend fun activateProfile(owner: SessionState.Active, id: String) = mutate(owner, ProxyWrite.Activate, id) { u, t -> gateway.activateProxyProfile(u, t, id) }
    suspend fun deleteProfile(owner: SessionState.Active, id: String) = mutate(owner, ProxyWrite.Delete, id) { u, t -> gateway.deleteProxyProfile(u, t, id) }
    suspend fun apply(owner: SessionState.Active, id: String, yaml: String) = mutate(owner, ProxyWrite.Configuration, id) { u, t -> gateway.applyProxyConfiguration(u, t, id, yaml) }
    suspend fun import(owner: SessionState.Active, request: ProxyImportRequest) = mutate(owner, ProxyWrite.Import, null) { u, t -> gateway.importProxySubscription(u, t, request) }
    suspend fun select(owner: SessionState.Active, group: ProxyGroup, proxy: String): ApiResult<Unit> {
        require(group.selectable && proxy in group.proxies)
        return mutate(owner, ProxyWrite.Selection, null) { u, t -> gateway.selectProxyNode(u, t, group.name, proxy) }
    }
    suspend fun routing(owner: SessionState.Active, mode: ProxyRoutingMode) = mutate(owner, ProxyWrite.Routing, null) { u, t -> gateway.setProxyRouting(u, t, mode) }
    suspend fun delay(owner: SessionState.Active, group: String, proxy: String, url: String, timeout: Int): ApiResult<ProxyDelay> {
        manage(owner); require(timeout in 1..60000)
        return read(owner) { u, t -> gateway.testProxyDelay(u, t, group, proxy, url, timeout) }
    }
    suspend fun acceptFacts(owner: SessionState.Active, pending: PendingProxyRequest): ApiResult<Unit> = mutations.withLock {
        verify(owner); require(pending in journal.pending(owner) && pending.action == null)
        val overview = overview(owner); val profiles = profiles(owner); val subscriptions = subscriptions(owner)
        val extra = when (pending.write) {
            ProxyWrite.Selection -> groups(owner); ProxyWrite.Routing -> routing(owner); ProxyWrite.Settings -> settings(owner)
            ProxyWrite.GeoData -> geoData(owner); ProxyWrite.Connection -> connections(owner); else -> ApiResult.Success(Unit)
        }
        if (listOf(overview, profiles, subscriptions, extra).all { it is ApiResult.Success }) {
            journal.complete(pending); ApiResult.Success(Unit)
        } else ApiResult.Transport(null)
    }
    private suspend fun <T> mutate(owner: SessionState.Active, write: ProxyWrite, target: String?, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = mutations.withLock {
        manage(owner); val pending = journal.begin(owner, null, target?.let(InstallationRoutes::canonicalId), write)
        read(owner, call).also { result ->
            if (result is ApiResult.Success || result is ApiResult.Problem && result.status in setOf(400, 401, 403, 404, 409)) journal.complete(pending)
        }
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); require(ServerCapabilities.PROXY in owner.capabilities)
        return session.authenticated { u, t -> verify(owner); call(u, t) }.also { verify(owner) }
    }
    private fun manage(owner: SessionState.Active) { verify(owner); require(owner.privilegedOperations && ServerCapabilities.PROXY in owner.capabilities) }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Proxy session changed") }
}
