package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Proxy Server reads and queued mutations.
 *
 * Every mutation is one host operation behind an idempotency key. The key is generated per submission
 * and deliberately not persisted: an interrupted submission is recovered through the operation id the
 * index recorded, which is the only handle the server actually honours (a replayed key with a different
 * body is refused as `idempotency-conflict`). Nothing here retains a secret — exports and subscription
 * tokens are returned to the caller and never stored.
 */
class ProxyServerRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val index: OperationIndex) {
    private val mutations = Mutex()

    suspend fun runtimeCatalog(owner: SessionState.Active, method: String, route: String, body: JsonBody? = null, key: String? = null) =
        read(owner) { u, t -> gateway.managedRuntime(u, t, method, route, body, key) }

    suspend fun snapshot(owner: SessionState.Active) = read(owner, gateway::proxyServerSnapshot)
    suspend fun releases(owner: SessionState.Active) = read(owner, gateway::proxyServerReleases)
    suspend fun validate(owner: SessionState.Active) = read(owner, gateway::proxyServerValidate)
    suspend fun exportFormats(owner: SessionState.Active, credentialId: String) =
        read(owner) { u, t -> gateway.proxyServerExportFormats(u, t, credentialId) }
    suspend fun export(owner: SessionState.Active, credentialId: String, request: ProxyServerExportRequest) =
        read(owner) { u, t -> gateway.proxyServerExport(u, t, credentialId, request) }

    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> {
        val canonical = InstallationRoutes.canonicalId(id)
        return read(owner) { u, t -> gateway.proxyServerOperation(u, t, canonical) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != canonical) return@let ApiResult.Transport(null)
                index.record(owner, OperationDomain.ProxyServer, result.value.kind, canonical)
            }
            result
        }
    }

    suspend fun publish(owner: SessionState.Active): ApiResult<ProxyOperation> =
        queued(owner, KIND_PUBLISH) { u, t, k -> gateway.proxyServerApply(u, t, k) }

    suspend fun rollback(owner: SessionState.Active): ApiResult<ProxyOperation> =
        queued(owner, KIND_ROLLBACK) { u, t, k -> gateway.proxyServerRollback(u, t, k) }

    suspend fun recover(owner: SessionState.Active): ApiResult<ProxyOperation> =
        queued(owner, KIND_RECOVER) { u, t, k -> gateway.proxyServerRecover(u, t, k) }

    suspend fun runtime(owner: SessionState.Active, action: ProxyServerRuntimeAction): ApiResult<ProxyOperation> =
        queued(owner, KIND_RUNTIME) { u, t, k -> gateway.proxyServerRuntime(u, t, action, k) }

    suspend fun install(owner: SessionState.Active): ApiResult<ProxyOperation> =
        queued(owner, KIND_RUNTIME) { u, t, k -> gateway.proxyServerInstall(u, t, k) }

    suspend fun upgrade(owner: SessionState.Active, version: String): ApiResult<ProxyOperation> =
        queued(owner, KIND_RUNTIME) { u, t, k -> gateway.proxyServerUpgrade(u, t, version, k) }

    suspend fun uninstall(owner: SessionState.Active, keepData: Boolean): ApiResult<ProxyOperation> =
        queued(owner, KIND_RUNTIME) { u, t, k -> gateway.proxyServerUninstall(u, t, keepData, k) }

    suspend fun saveConfiguration(owner: SessionState.Active, request: SaveProxyServerConfiguration): ApiResult<ProxyOperation> =
        queued(owner, "proxy_server.configuration") { u, t, k -> gateway.proxyServerSaveConfiguration(u, t, request, k) }

    suspend fun savePrincipal(owner: SessionState.Active, id: String?, request: SaveProxyServerPrincipal): ApiResult<ProxyOperation> =
        queued(owner, KIND_PRINCIPAL) { u, t, k -> gateway.proxyServerSavePrincipal(u, t, id, request, k) }

    suspend fun deletePrincipal(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> =
        queued(owner, KIND_PRINCIPAL) { u, t, k -> gateway.proxyServerDeletePrincipal(u, t, id, k) }

    suspend fun saveCredential(owner: SessionState.Active, id: String?, request: SaveProxyServerCredential): ApiResult<ProxyOperation> =
        queued(owner, KIND_CREDENTIAL) { u, t, k -> gateway.proxyServerSaveCredential(u, t, id, request, k) }

    suspend fun rotateCredential(owner: SessionState.Active, id: String, request: RotateProxyServerCredential): ApiResult<ProxyOperation> =
        queued(owner, KIND_CREDENTIAL) { u, t, k -> gateway.proxyServerRotateCredential(u, t, id, request, k) }

    suspend fun deleteCredential(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> =
        queued(owner, KIND_CREDENTIAL) { u, t, k -> gateway.proxyServerDeleteCredential(u, t, id, k) }

    /** The token is returned to the caller exactly once; nothing here stores or re-reads it. */
    suspend fun issueSubscription(owner: SessionState.Active, request: IssueProxyServerSubscription): ApiResult<ProxyServerSubscriptionIssued> =
        mutations.withLock {
            manage(owner)
            read(owner) { u, t -> gateway.proxyServerIssueSubscription(u, t, request, newKey()) }
        }

    suspend fun revokeSubscription(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> =
        queued(owner, KIND_SUBSCRIPTION) { u, t, k -> gateway.proxyServerRevokeSubscription(u, t, id, k) }

    private suspend fun queued(owner: SessionState.Active, kind: String,
        call: suspend (String, String, String) -> ApiResult<String>): ApiResult<ProxyOperation> = mutations.withLock {
        manage(owner)
        when (val accepted = read(owner) { u, t -> call(u, t, newKey()) }) {
            is ApiResult.Success -> {
                // Record before querying: a failed status read must not lose an accepted operation.
                index.record(owner, OperationDomain.ProxyServer, kind, accepted.value)
                operation(owner, accepted.value)
            }
            is ApiResult.Problem -> accepted
            is ApiResult.Transport -> accepted
        }
    }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); require(ServerCapabilities.PROXY_SERVER in owner.capabilities)
        return session.authenticated { u, t -> verify(owner); call(u, t) }.also { verify(owner) }
    }

    private fun manage(owner: SessionState.Active) {
        verify(owner)
        require(owner.privilegedOperations && ServerCapabilities.PROXY_SERVER in owner.capabilities)
    }

    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Proxy Server session changed")
    }

    private fun newKey() = java.util.UUID.randomUUID().toString()

    /** Operation kinds this client records, so an interrupted submission stays reachable. */
    private companion object {
        const val KIND_PUBLISH = "proxy_server.publish"
        const val KIND_ROLLBACK = "proxy_server.rollback"
        const val KIND_RECOVER = "proxy_server.recover"
        const val KIND_RUNTIME = "proxy_server.runtime"
        const val KIND_PRINCIPAL = "proxy_server.principal"
        const val KIND_CREDENTIAL = "proxy_server.credential"
        const val KIND_SUBSCRIPTION = "proxy_server.subscription"
    }
}
