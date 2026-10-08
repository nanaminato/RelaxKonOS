package app.relaxkonos.mobile.servercenter

import java.net.URI
import java.util.UUID
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** Process-only preferences and handles. Never synchronized, persisted, or auto-reconnected. */
data class SshLocalForwardRequest(val remotePort: Int, val preferredLocalPort: Int? = null, val scheme: String = "http", val pathAndQuery: String = "/") {
    fun valid(): Boolean = remotePort in 1..65535 && (preferredLocalPort == null || preferredLocalPort in 1024..65535) &&
        scheme in setOf("http", "https") && pathAndQuery.length <= 2048 && pathAndQuery.startsWith("/") && !pathAndQuery.startsWith("//") &&
        pathAndQuery.none { it.isISOControl() || it == '\\' } && runCatching {
            val uri = URI(pathAndQuery); !uri.isAbsolute && uri.rawAuthority == null && uri.fragment == null
        }.getOrDefault(false)
    fun localUrl(port: Int): String { require(valid()); require(port in 1..65535); return "$scheme://127.0.0.1:$port$pathAndQuery" }
}
enum class SshForwardStatus { Running, Stopped, Disconnected }
data class SshLocalForward(val id: String, val hostId: String, val request: SshLocalForwardRequest, val localPort: Int,
    val status: SshForwardStatus, val startedAtMillis: Long, val testedAtMillis: Long? = null, val reachable: Boolean? = null)
data class SshForwardState(val hostId: String? = null, val items: List<SshLocalForward> = emptyList(), val busy: Boolean = false, val problem: String? = null)

/** Dedicated sessions make it impossible for a user row to close the managed-login tunnel. */
class SshLocalForwardManager(
    private val resolver: ServerCenterConnectionResolver,
    private val targets: ServerHostTargetStore,
    private val passwordCopy: (String) -> CharArray?,
    private val workspaceHost: () -> String?,
    private val scope: CoroutineScope,
    private val startKeeper: (Int) -> Unit,
    private val stopKeeper: () -> Unit,
) {
    private data class Handle(val session: ServerCenterHostSession, val tunnel: ServerCenterSshTunnel)
    private val handles = mutableMapOf<String, Handle>()
    private val mutable = MutableStateFlow(SshForwardState())
    val state = mutable.asStateFlow()
    private var version = 0
    private var keeperLease = 0
    val lease: Int get() = keeperLease
    private var job: Job? = null
    private var observer: Job? = null

    fun start(hostId: String, request: SshLocalForwardRequest, replacingId: String? = null, onComplete: (Boolean) -> Unit = {}) {
        if (mutable.value.busy || workspaceHost() != hostId) { onComplete(false); return }
        if (!request.valid()) { mutable.update { it.copy(problem = "invalid") }; onComplete(false); return }
        if (mutable.value.hostId != null && mutable.value.hostId != hostId) stopAll()
        val old = replacingId?.let { id -> mutable.value.items.firstOrNull { it.id == id && it.hostId == hostId } }
        if (replacingId != null && old == null) { onComplete(false); return }
        if (old == null && mutable.value.items.size >= 16) { mutable.update { it.copy(problem = "limit") }; onComplete(false); return }
        val target = targets.find(hostId) ?: run { onComplete(false); return }
        val secret = passwordCopy(hostId) ?: run { mutable.update { it.copy(problem = "credential") }; onComplete(false); return }
        // Explicit edits restart this row; if replacement fails, its old listener stays stopped.
        if (old != null) closeRow(old.id, SshForwardStatus.Stopped)
        val epoch = ++version
        mutable.update { it.copy(hostId = hostId, busy = true, problem = null) }
        var completionReported = false
        job = scope.launch {
            var started = false
            var connection: ServerCenterHostSession? = null; var tunnel: ServerCenterSshTunnel? = null
            try {
                keeperLease++; startKeeper(keeperLease)
                val connected = resolver.connect(target, SshCredential(SshCredentialKind.Password, secret, null), resolver.prepareHostKeyGuard(target))
                connection = connected
                if (!current(hostId, epoch)) return@launch
                val listener = withContext(Dispatchers.IO) { connected.sshTransport.openLocalForward(request.remotePort, request.preferredLocalPort).also { tunnel = it } }
                tunnel = listener
                if (!current(hostId, epoch)) return@launch
                val id = old?.id ?: UUID.randomUUID().toString()
                handles[id] = Handle(connected, listener)
                val row = SshLocalForward(id, hostId, request, listener.localPort, SshForwardStatus.Running, System.currentTimeMillis())
                connection = null; tunnel = null
                mutable.update { it.copy(items = it.items.filterNot { item -> item.id == id } + row) }
                started = true
                observe()
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: ServerCenterHostKeyRejectedException) { if (current(hostId, epoch)) mutable.update { it.copy(problem = "trust") } }
            catch (_: Exception) { if (current(hostId, epoch)) mutable.update { it.copy(problem = "connect") } }
            finally {
                secret.fill('\u0000'); tunnel?.close(); connection?.close()
                if (version == epoch) {
                    mutable.update { it.copy(busy = false) }
                    if (handles.isEmpty()) stopKeeper()
                }
                completionReported = true
                onComplete(started && current(hostId, epoch))
            }
        }.also { it.invokeOnCompletion {
            secret.fill('\u0000')
            if (!completionReported) {
                if (version == epoch) mutable.update { state -> state.copy(busy = false) }
                onComplete(false)
            }
        } }
    }
    fun stop(id: String) {
        if (mutable.value.busy) return
        closeRow(id, SshForwardStatus.Stopped)
        if (handles.isEmpty()) { observer?.cancel(); observer = null; stopKeeper() }
    }
    fun remove(id: String) {
        if (mutable.value.busy) return
        stop(id); mutable.update { it.copy(items = it.items.filterNot { row -> row.id == id }) }
    }
    fun stopAll() {
        version++; keeperLease++; job?.cancel(); observer?.cancel(); observer = null
        handles.values.toList().forEach { it.tunnel.close(); it.session.close() }; handles.clear()
        mutable.update { it.copy(busy = false, items = it.items.map { row -> row.copy(status = SshForwardStatus.Stopped, reachable = null, testedAtMillis = null) }) }
        stopKeeper()
    }
    fun keeperFailed(lease: Int) { if (lease == keeperLease) { stopAll(); mutable.update { it.copy(problem = "service") } } }
    fun clearWorkspace() { stopAll(); mutable.value = SshForwardState() }
    fun test(id: String) {
        if (mutable.value.busy) return
        val row = mutable.value.items.firstOrNull { it.id == id && it.status == SshForwardStatus.Running } ?: return
        val handle = handles[id] ?: return; val epoch = ++version
        mutable.update { it.copy(busy = true, problem = null) }
        job = scope.launch {
            try {
                val reachable = handle.session.sshTransport.testLoopbackPort(row.request.remotePort)
                if (current(row.hostId, epoch)) mutable.update { it.copy(items = it.items.map { item -> if (item.id == id) item.copy(reachable = reachable, testedAtMillis = System.currentTimeMillis()) else item }) }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(row.hostId, epoch)) mutable.update { it.copy(problem = "test") } }
            finally { if (version == epoch) mutable.update { it.copy(busy = false) } }
        }
    }
    private fun closeRow(id: String, status: SshForwardStatus) {
        handles.remove(id)?.let { it.tunnel.close(); it.session.close() }
        mutable.update { it.copy(items = it.items.map { row -> if (row.id == id) row.copy(status = status, reachable = null, testedAtMillis = null) else row }) }
    }
    private fun current(hostId: String, epoch: Int) = workspaceHost() == hostId && mutable.value.hostId == hostId && version == epoch
    private fun observe() {
        if (observer?.isActive == true) return
        observer = scope.launch {
            while (isActive) {
                delay(3_000)
                if (mutable.value.busy) continue
                handles.filterValues { !it.session.sshTransport.isConnected }.keys.toList().forEach { closeRow(it, SshForwardStatus.Disconnected) }
                if (handles.isEmpty()) { stopKeeper(); break }
            }
            observer = null
        }
    }
}
