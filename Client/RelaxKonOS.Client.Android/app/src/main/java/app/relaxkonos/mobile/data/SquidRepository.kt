package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID

/** Pending requests survive process death. Replays retain their original body and idempotency key. */
data class PendingSquidRequest(val key: String, val request: SquidRequest, val operationId: String? = null)
class SquidRepository(private val gateway: RelaxKonGateway, private val session: AuthSession, private val directory: File) {
    private val mutations = Mutex()
    suspend fun snapshot(owner: SessionState.Active) = read(owner, gateway::squidConfiguration)
    suspend fun preview(owner: SessionState.Active, configuration: SquidConfiguration) = read(owner) { u, t -> gateway.squidPreview(u, t, configuration) }
    suspend fun validate(owner: SessionState.Active, configuration: SquidConfiguration) = read(owner) { u, t -> gateway.squidValidate(u, t, configuration) }
    suspend fun pending(owner: SessionState.Active): PendingSquidRequest? = withContext(Dispatchers.IO) {
        verify(owner)
        val file = file(owner)
        if (!file.exists()) null else JSONObject(file.readText()).let {
            PendingSquidRequest(it.getString("key"), SquidWire.request(it.getJSONObject("request")),
                if (it.isNull("operationId")) null else InstallationRoutes.canonicalId(it.getString("operationId")))
        }
    }
    suspend fun begin(owner: SessionState.Active, request: SquidRequest): ApiResult<String> = mutations.withLock {
        verify(owner); require(owner.privilegedOperations && owner.controller)
        require(pending(owner) == null)
        val pending = PendingSquidRequest(UUID.randomUUID().toString(), request)
        save(owner, pending)
        submit(owner, pending)
    }
    suspend fun resume(owner: SessionState.Active): ApiResult<String> = mutations.withLock {
        verify(owner)
        val pending = requireNotNull(pending(owner))
        pending.operationId?.let { return@withLock ApiResult.Success(it) }
        require(owner.privilegedOperations && owner.controller)
        submit(owner, pending)
    }
    private suspend fun submit(owner: SessionState.Active, pending: PendingSquidRequest): ApiResult<String> {
        val result = read(owner) { u, t -> gateway.squidQueue(u, t, pending.request, pending.key) }
        if (result is ApiResult.Success) save(owner, pending.copy(operationId = InstallationRoutes.canonicalId(result.value)))
        // Explicit pre-queue rejections are definitive; transport and 5xx remain replayable.
        else if (result is ApiResult.Problem && result.status in setOf(400, 401, 403, 404)) clear(owner)
        return result
    }
    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<ProxyOperation> = mutations.withLock {
        val canonical = InstallationRoutes.canonicalId(id)
        val result = read(owner) { u, t -> gateway.proxyOperation(u, t, canonical) }
        if (result is ApiResult.Success) {
            if (result.value.operationId != canonical || !result.value.kind.startsWith("squid.")) return@withLock ApiResult.Transport("squid.response.invalid")
            if (!result.value.state.active && pending(owner)?.operationId == canonical) clear(owner)
        }
        result
    }
    private suspend fun save(owner: SessionState.Active, pending: PendingSquidRequest) = withContext(Dispatchers.IO) {
        verify(owner); directory.mkdirs()
        val target = file(owner); val staged = File(target.path + ".new")
        try {
            val text = JSONObject().put("key", pending.key).put("request", pending.request.json()).put("operationId", pending.operationId ?: JSONObject.NULL).toString()
            java.io.FileOutputStream(staged).use { it.write(text.toByteArray()); it.fd.sync() }
            check(staged.renameTo(target))
        } finally { staged.delete() }
    }
    private suspend fun clear(owner: SessionState.Active) = withContext(Dispatchers.IO) { verify(owner); val target = file(owner); check(!target.exists() || target.delete()) }
    private fun file(owner: SessionState.Active): File {
        val identity = JSONObject().put("service", owner.serviceId).put("user", owner.userName).put("workspace", owner.workspaceId).toString()
        val key = MessageDigest.getInstance("SHA-256").digest(identity.toByteArray()).joinToString("") { "%02x".format(it) }
        return File(directory, "squid-$key.json")
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return session.authenticated { url, token -> verify(owner); call(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Squid session changed")
        require(ServerCapabilities.GATEWAY_PROXY in owner.capabilities)
    }
}
