package app.relaxkonos.mobile.servercenter

import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class SshTerminalUiState(
    val sessionId: String = "",
    val hostId: String = "",
    val createdAtMillis: Long = 0,
    val connecting: Boolean = false,
    val connected: Boolean = false,
    val problem: Boolean = false,
    val output: String = "",
    val draft: String = "",
    val concealInput: Boolean = false,
    val fontSize: Int = 13,
)

data class SshTerminalSessionsState(
    val sessions: List<SshTerminalUiState> = emptyList(),
    val selected: Map<String, String> = emptyMap(),
) {
    fun selectedForHost(hostId: String): SshTerminalUiState? =
        sessions.firstOrNull { it.hostId == hostId && it.sessionId == selected[hostId] }
}

/** Owns both the PTY and its dedicated SSH transport. Closing one never closes another. */
class SshTerminalConnection(val terminal: ServerCenterSshTerminal, private val owner: AutoCloseable) : AutoCloseable {
    private val closed = AtomicBoolean()
    override fun close() {
        if (closed.compareAndSet(false, true)) {
            try { terminal.close() } finally { owner.close() }
        }
    }
}

fun interface SshTerminalConnector {
    suspend fun connect(hostId: String): SshTerminalConnection
}

/** Main-thread confined, application-owned sessions. Screens only observe/select; never dispose. */
class SshTerminalSessions(
    private val scope: CoroutineScope,
    private val connector: SshTerminalConnector,
    private val hostExists: (String) -> Boolean,
    private val now: () -> Long = System::currentTimeMillis,
) {
    // SSH shells cannot survive this process. Every application instance starts with no sessions.
    private val mutableState = MutableStateFlow(SshTerminalSessionsState())
    val state = mutableState.asStateFlow()
    private val runtimes = mutableMapOf<String, Runtime>()

    private class Runtime(scope: CoroutineScope) {
        val job = SupervisorJob(scope.coroutineContext[Job])
        val scope = CoroutineScope(scope.coroutineContext + job)
        var connection: SshTerminalConnection? = null
    }

    /** Re-entering a page never reconnects or replaces an existing shell. */
    fun enter(hostId: String) {
        if (mutableState.value.selectedForHost(hostId) != null) return
        val existing = mutableState.value.sessions.firstOrNull { it.hostId == hostId }
        if (existing != null) select(existing.sessionId) else create(hostId)
    }

    fun create(hostId: String): String? {
        if (!hostExists(hostId) || mutableState.value.sessions.size >= MAX_SESSIONS) return null
        val id = UUID.randomUUID().toString()
        val previous = mutableState.value
        mutableState.value = previous.copy(
            sessions = previous.sessions + SshTerminalUiState(id, hostId, now()),
            selected = previous.selected + (hostId to id),
        )
        reconnect(id)
        return id
    }

    fun select(id: String) {
        val session = find(id) ?: return
        mutableState.value = mutableState.value.copy(selected = mutableState.value.selected + (session.hostId to id))
    }

    fun updateDraft(id: String, value: String) { update(id) { it.copy(draft = value) } }
    fun concealInput(id: String, value: Boolean) { update(id) { it.copy(concealInput = value) } }
    fun fontSize(id: String, value: Int) { update(id) { it.copy(fontSize = value.coerceIn(9, 24)) } }

    fun reconnect(id: String) {
        val session = find(id) ?: return
        if (session.connected || session.connecting || !hostExists(session.hostId)) return
        stopRuntime(id)
        val runtime = Runtime(scope)
        runtimes[id] = runtime
        update(id) { it.copy(connecting = true, connected = false, problem = false, output = "") }
        runtime.scope.launch {
            try {
                val connection = connector.connect(session.hostId)
                runtime.connection = connection
                if (!current(id, runtime)) return@launch
                update(id) { it.copy(connecting = false, connected = true) }
                while (current(id, runtime)) {
                    val output = connection.terminal.read() ?: break
                    // Keep VT sequences intact. The terminal emulator owns parsing and screen state.
                    if (current(id, runtime)) update(id) { it.copy(output = it.output + output) }
                }
                if (current(id, runtime)) update(id) { it.copy(connecting = false, connected = false) }
            } catch (_: CancellationException) {
                // Only ending/replacing this session cancels its jobs; navigation does not.
            } catch (_: Exception) {
                if (current(id, runtime)) update(id) { it.copy(connecting = false, connected = false, problem = true) }
            } finally {
                val connection = runtime.connection
                runtime.connection = null
                withContext(NonCancellable + Dispatchers.IO) { runCatching { connection?.close() } }
            }
        }
    }

    fun send(id: String, value: String) = operate(id) { it.write(value) }
    fun resize(id: String, columns: Int, rows: Int) = operate(id) {
        it.resize(columns.coerceIn(2, 500), rows.coerceIn(1, 200))
    }

    private fun operate(id: String, action: suspend (ServerCenterSshTerminal) -> Unit) {
        val runtime = runtimes[id] ?: return
        val connection = runtime.connection ?: return
        if (find(id)?.connected != true) return
        runtime.scope.launch {
            try {
                if (current(id, runtime)) action(connection.terminal)
            } catch (_: CancellationException) {
                // Explicit close cancels pending input and dimensions.
            } catch (_: Exception) {
                if (current(id, runtime)) {
                    update(id) { it.copy(connected = false, connecting = false, problem = true) }
                    stopRuntime(id)
                }
            }
        }
    }

    /** Explicit end removes the in-memory entry and releases its network resources. */
    fun end(id: String) {
        val session = find(id) ?: return
        stopRuntime(id)
        val previous = mutableState.value
        val remaining = previous.sessions.filterNot { it.sessionId == id }
        val selected = previous.selected.toMutableMap()
        if (selected[session.hostId] == id) {
            val next = remaining.firstOrNull { it.hostId == session.hostId }
            if (next == null) selected.remove(session.hostId) else selected[session.hostId] = next.sessionId
        }
        mutableState.value = previous.copy(sessions = remaining, selected = selected)
    }

    private fun find(id: String) = mutableState.value.sessions.firstOrNull { it.sessionId == id }
    private fun current(id: String, runtime: Runtime) = runtimes[id] === runtime && find(id) != null
    private fun update(id: String, change: (SshTerminalUiState) -> SshTerminalUiState) {
        mutableState.value = mutableState.value.copy(sessions = mutableState.value.sessions.map {
            if (it.sessionId == id) change(it) else it
        })
    }

    private fun stopRuntime(id: String) {
        val runtime = runtimes.remove(id) ?: return
        runtime.job.cancel()
        // Closing the transport unblocks its blocking IO reader even when cancellation alone cannot.
        val connection = runtime.connection
        scope.launch(Dispatchers.IO) { runCatching { connection?.close() } }
    }

    companion object {
        const val MAX_SESSIONS = 100
    }
}
