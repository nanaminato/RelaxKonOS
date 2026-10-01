package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ServerTerminalConnection
import app.relaxkonos.mobile.core.net.TerminalConnection
import app.relaxkonos.mobile.core.net.TerminalSessionSummary
import app.relaxkonos.mobile.core.net.terminalConnectionFailure
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

internal fun interface TerminalConnectionFactory {
    fun create(url: String, token: String, output: (ByteArray) -> Unit, exit: (Int) -> Unit, disconnect: () -> Unit): TerminalConnection
}

/** Runs on the UI scope; SignalR callbacks enter that scope before touching session state. */
internal class ServerTerminalController(
    private val auth: AuthSession,
    private val scope: CoroutineScope,
    private val factory: TerminalConnectionFactory = TerminalConnectionFactory { url, token, output, exit, disconnect ->
        ServerTerminalConnection(url, { token }, output, exit, disconnect)
    },
) {
    private val mutable = MutableStateFlow(ServerTerminalState())
    val state = mutable.asStateFlow()
    private var transport: TerminalConnection? = null
    private var connectionJob: Job? = null
    private var operationJob: Job? = null
    private var resizeJob: Job? = null
    private val inputMutex = Mutex()
    private var generation = 0
    private var owner: SessionState.Active? = null
    private var selectedId: String? = null
    private var transcript = TerminalTranscript()
    private var columns = 80
    private var rows = 24
    private var appliedSize = columns to rows
    private var resizeRevision = 0

    fun connect(active: SessionState.Active) {
        if (owner != null && owner !== active) resetOwner()
        if (connectionJob?.isActive == true || mutable.value.connected) return
        owner = active
        val current = ++generation
        operationJob?.cancel()
        resizeJob?.cancel()
        val old = transport
        transport = null
        mutable.update { it.copy(connecting = true, connected = false, busy = false, error = false, retryAttempt = 0) }
        connectionJob = scope.launch {
            dispose(old)
            // Bounded recovery for expiry and brief network loss. Commands are never replayed.
            val delays = listOf(0L, 1_000L, 2_000L, 5_000L)
            for ((attempt, wait) in delays.withIndex()) {
                if (!isCurrent(current, active)) return@launch
                mutable.update { it.copy(retryAttempt = attempt) }
                delay(wait)
                var connection: TerminalConnection? = null
                var connectionClosed = false
                try {
                    when (auth.connectionToken()) {
                        is ApiResult.Success -> Unit
                        is ApiResult.Problem -> break
                        is ApiResult.Transport -> error("Could not renew terminal authentication.")
                    }
                    val opened = auth.authenticated { url, token ->
                        if (!isCurrent(current, active) || url != active.effectiveBaseUrl) {
                            return@authenticated ApiResult.Transport("The terminal owner changed.")
                        }
                        var candidate: TerminalConnection? = null
                        connectionClosed = false
                        val created = factory.create(url, token,
                            { bytes -> scope.launch {
                                if (current == generation && transport === candidate) {
                                    mutable.update { it.copy(output = transcript.append(bytes), frame = transcript.frame) }
                                    val responses = transcript.drainResponses()
                                    if (responses.isNotEmpty()) inputMutex.withLock {
                                        if (current == generation && transport === candidate) {
                                            try { candidate?.input(responses.joinToString("").toByteArray(Charsets.UTF_8)) }
                                            catch (cancelled: CancellationException) { throw cancelled }
                                            catch (_: Exception) { if (current == generation) mutable.update { it.copy(error = true) } }
                                        }
                                    }
                                }
                            } },
                            { code -> scope.launch {
                                if (current == generation && transport === candidate) mutable.update {
                                    it.copy(exitCode = code, sessions = it.sessions.filterNot { session -> session.sessionId == it.sessionId })
                                }
                            } },
                            { scope.launch {
                                if (current == generation && connection === candidate) connectionClosed = true
                                if (current == generation && transport === candidate && mutable.value.connected) {
                                    mutable.update { it.copy(connected = false, busy = false) }
                                    connectionJob?.cancel()
                                    connectionJob = null
                                    connect(active)
                                }
                            } })
                        candidate = created
                        connection = created
                        try {
                            created.connect()
                            ApiResult.Success(created)
                        } catch (cancelled: CancellationException) {
                            dispose(created)
                            connection = null
                            throw cancelled
                        } catch (error: Exception) {
                            dispose(created)
                            connection = null
                            terminalConnectionFailure(error)
                        }
                    }
                    if (opened is ApiResult.Problem) { dispose(connection); break }
                    if (opened !is ApiResult.Success) error("Terminal handshake failed.")
                    val activeConnection = opened.value
                    connection = activeConnection
                    if (!isCurrent(current, active)) { dispose(connection); return@launch }
                    val sessions = activeConnection.sessions().filterNot { it.hasExited }
                    val saved = selectedId
                    val target = when {
                        saved != null -> sessions.firstOrNull { it.sessionId == saved }?.sessionId
                        !mutable.value.sessionLost -> sessions.firstOrNull()?.sessionId
                        else -> null
                    }
                    mutable.update { it.copy(sessions = sessions,
                        sessionLost = (saved != null && target == null) || (saved == null && it.sessionLost)) }
                    transport = activeConnection
                    if (target != null) attachTo(activeConnection, target, current)
                    else if (saved != null) {
                        selectedId = null
                        mutable.update { it.copy(sessionId = null, output = "", frame = TerminalRenderFrame(), exitCode = null) }
                    }
                    if (connectionClosed) error("The terminal disconnected while attaching.")
                    mutable.update { it.copy(connecting = false, connected = true, error = false, retryAttempt = 0) }
                    scheduleResize()
                    return@launch
                } catch (cancelled: CancellationException) {
                    dispose(connection)
                    throw cancelled
                } catch (_: Exception) {
                    dispose(connection)
                    if (current != generation) return@launch
                    transport = null
                    if (auth.state.value !is SessionState.Active) break
                }
            }
            if (current == generation) mutable.update { it.copy(connecting = false, connected = false, error = true) }
        }
    }

    private fun isCurrent(current: Int, active: SessionState.Active) = current == generation && auth.state.value === active

    private suspend fun dispose(connection: TerminalConnection?) = withContext(NonCancellable) {
        runCatching { connection?.disconnect() }
        Unit
    }

    private suspend fun attachTo(connection: TerminalConnection, id: String?, current: Int) {
        resizeJob?.cancel()
        resizeJob = null
        transcript = TerminalTranscript().also { it.resize(columns, rows) }
        mutable.update { it.copy(output = "", frame = TerminalRenderFrame(), sessionLost = false, sessionId = null, exitCode = null) }
        val attachColumns = columns
        val attachRows = rows
        appliedSize = attachColumns to attachRows
        val result = connection.attach(id, attachColumns, attachRows)
        if (current != generation) return
        selectedId = result.sessionId
        mutable.update { it.copy(sessionId = result.sessionId) }
    }

    private fun operate(action: suspend (TerminalConnection, Int) -> Unit) {
        val connection = transport ?: return
        if (!mutable.value.connected || mutable.value.busy) return
        val current = generation
        mutable.update { it.copy(busy = true, error = false) }
        operationJob = scope.launch {
            try { inputMutex.withLock { if (current == generation) action(connection, current) } }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current == generation) mutable.update { it.copy(error = true) } }
            finally { if (current == generation) {
                mutable.update { it.copy(busy = false) }
                scheduleResize()
            } }
        }
    }

    fun attach(id: String?) = operate { connection, current ->
        val sessions = connection.sessions().filterNot { it.hasExited }
        if (current != generation) return@operate
        if (id != null && sessions.none { it.sessionId == id }) {
            mutable.update { it.copy(sessions = sessions, error = true) }
            return@operate
        }
        attachTo(connection, id, current)
        val refreshed = connection.sessions().filterNot { it.hasExited }
        if (current == generation) mutable.update { it.copy(sessions = refreshed) }
    }

    /** False means input was not accepted. A failed or disconnected write is never retried. */
    fun send(text: String): Boolean {
        val connection = transport ?: return false
        if (!mutable.value.canInput) return false
        val current = generation
        val sessionId = selectedId
        scope.launch {
            inputMutex.withLock {
                if (current != generation || sessionId != selectedId || !mutable.value.connected || mutable.value.exitCode != null) return@withLock
                try { connection.input(text.toByteArray(Charsets.UTF_8)) }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { if (current == generation) mutable.update { it.copy(error = true) } }
            }
        }
        return true
    }

    fun resize(newColumns: Int, newRows: Int) {
        val nextColumns = newColumns.coerceIn(20, 300)
        val nextRows = newRows.coerceIn(5, 100)
        if (columns == nextColumns && rows == nextRows) return
        columns = nextColumns
        rows = nextRows
        resizeRevision++
        scheduleResize()
    }

    private fun scheduleResize() {
        val connection = transport ?: return
        if (!mutable.value.canInput || resizeJob?.isActive == true || appliedSize == (columns to rows)) return
        val current = generation
        val sessionId = selectedId
        fun ready() = current == generation && transport === connection && selectedId == sessionId && mutable.value.canInput
        resizeJob = scope.launch {
            var failed = false
            try {
                while (ready() && appliedSize != (columns to rows)) {
                    val revision = resizeRevision
                    // IME animation changes the viewport on every frame. Keep parsing at the
                    // negotiated size until the layout settles, and never overlap remote resizes.
                    delay(200)
                    if (revision != resizeRevision) continue
                    inputMutex.withLock {
                        if (!ready() || revision != resizeRevision || appliedSize == (columns to rows)) return@withLock
                        val size = columns to rows
                        mutable.update { it.copy(output = transcript.resize(size.first, size.second), frame = transcript.frame) }
                        connection.resize(size.first, size.second)
                        appliedSize = size
                    }
                }
            }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) {
                failed = true
                if (current == generation) mutable.update { it.copy(error = true) }
            }
            finally {
                if (resizeJob === coroutineContext[Job]) {
                    resizeJob = null
                    if (!failed) scheduleResize()
                }
            }
        }
    }

    fun close(sessionId: String) = operate { connection, current ->
        connection.closeSession(sessionId)
        val sessions = connection.sessions().filterNot { it.hasExited }
        if (current != generation) return@operate
        mutable.update { it.copy(sessions = sessions) }
        if (sessionId == selectedId) {
            selectedId = null
            mutable.update { it.copy(sessionId = null, output = "", frame = TerminalRenderFrame(), exitCode = null) }
            sessions.firstOrNull()?.let { attachTo(connection, it.sessionId, current) }
        }
    }

    fun closeSessions(ids: List<String>) = operate { connection, current ->
        val targets = ids.distinct().filter { id -> mutable.value.sessions.any { it.sessionId == id } }
        var failure = false
        targets.forEach { id ->
            if (current != generation) return@operate
            try { connection.closeSession(id) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { failure = true }
        }
        val sessions = connection.sessions().filterNot { it.hasExited }
        if (current == generation) {
            mutable.update { it.copy(sessions = sessions, error = failure) }
            if (selectedId in targets && sessions.none { it.sessionId == selectedId }) {
                selectedId = null
                mutable.update { it.copy(sessionId = null, output = "", frame = TerminalRenderFrame(), exitCode = null) }
                sessions.firstOrNull()?.let { attachTo(connection, it.sessionId, current) }
            }
        }
    }

    /** Local display action; no shell input, reconnect or remote history mutation. */
    fun clearOutput() { transcript.clearLocal(); mutable.update { it.copy(output = "", frame = transcript.frame) } }
    fun resetOwner() {
        detach(); owner = null; selectedId = null; transcript = TerminalTranscript()
        mutable.value = ServerTerminalState()
    }

    fun detach() {
        generation++
        resizeJob?.cancel()
        connectionJob?.cancel()
        operationJob?.cancel()
        val previous = transport
        transport = null
        mutable.update { it.copy(connected = false, connecting = false, busy = false) }
        // ViewModel.onCleared may run after its scope was cancelled. Stopping the transport still
        // has to complete, and disconnect() has a bounded timeout.
        scope.launch(NonCancellable) { dispose(previous) }
    }
}

data class ServerTerminalState(
    val connecting: Boolean = false,
    val connected: Boolean = false,
    val busy: Boolean = false,
    val retryAttempt: Int = 0,
    val error: Boolean = false,
    val sessionLost: Boolean = false,
    val sessionId: String? = null,
    val sessions: List<TerminalSessionSummary> = emptyList(),
    val output: String = "",
    val frame: TerminalRenderFrame = TerminalRenderFrame(output),
    val exitCode: Int? = null,
) {
    val canInput: Boolean get() = connected && !busy && sessionId != null && exitCode == null
}
