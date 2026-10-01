package app.relaxkonos.mobile.ui.manage.guardian

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

internal enum class GuardianLogPhase { Idle, Connecting, Live, Failed }
internal data class GuardianLogState(val phase: GuardianLogPhase = GuardianLogPhase.Idle,
    val logs: List<GuardianLog> = emptyList(), val truncated: Boolean = false)
internal fun interface GuardianLogConnectionFactory {
    fun create(url: String, token: String, snapshot: (List<GuardianLog>) -> Unit, closed: () -> Unit): GuardianLogConnection
}

/** One current login and workload; reconnects only the read subscription, with bounded retries. */
internal class GuardianLogObserver(private val auth: AuthSession, private val scope: CoroutineScope,
    private val factory: GuardianLogConnectionFactory = GuardianLogConnectionFactory { url, token, snapshot, closed ->
        ServerGuardianLogConnection(url, token, snapshot, closed)
    }) {
    private val mutable = MutableStateFlow(GuardianLogState())
    val state = mutable.asStateFlow()
    private var generation = 0
    private var job: Job? = null
    private var owner: SessionState.Active? = null
    private var selected: String? = null
    private var transport: GuardianLogConnection? = null
    fun observe(active: SessionState.Active, id: String?) {
        if (owner === active && selected == id && job?.isActive == true) return
        stop(); owner = active; selected = id
        if (id == null || auth.state.value !== active) return
        val version = generation
        fun current() = generation == version && auth.state.value === active
        mutable.value = GuardianLogState(GuardianLogPhase.Connecting)
        job = scope.launch {
            for (wait in listOf(0L, 1_000L, 2_000L, 5_000L)) {
                delay(wait); if (!current()) return@launch
                var disconnected = CompletableDeferred<Unit>()
                var connection: GuardianLogConnection? = null
                var snapshots = 0
                try {
                    if (auth.connectionToken() !is ApiResult.Success) error("Guardian authentication unavailable")
                    val result = auth.authenticated { url, token ->
                        if (!current() || url != active.effectiveBaseUrl) throw CancellationException("Guardian owner changed")
                        val candidateClosed = CompletableDeferred<Unit>()
                        disconnected = candidateClosed
                        var callbackConnection: GuardianLogConnection? = null
                        val candidate = factory.create(url, token, { logs -> scope.launch {
                            if (current() && transport === callbackConnection) {
                                snapshots++
                                runCatching { boundedGuardianLogs(logs) }.onSuccess { (values, clipped) ->
                                    mutable.value = GuardianLogState(GuardianLogPhase.Live, values, clipped)
                                }.onFailure { mutable.update { it.copy(phase = GuardianLogPhase.Failed) }; candidateClosed.complete(Unit) }
                            }
                        } }, { scope.launch { if (current() && transport === callbackConnection) candidateClosed.complete(Unit) } })
                        callbackConnection = candidate; connection = candidate; transport = candidate
                        try {
                            candidate.connect()
                            val revision = snapshots
                            val initial = candidate.subscribe(id)
                            if (current() && snapshots == revision) {
                                val (values, clipped) = boundedGuardianLogs(initial)
                                mutable.value = GuardianLogState(GuardianLogPhase.Live, values, clipped)
                            }
                            ApiResult.Success(Unit)
                        } catch (cancelled: CancellationException) { throw cancelled }
                        catch (error: Exception) { if (transport === candidate) transport = null; connection = null; dispose(candidate); terminalConnectionFailure(error) }
                    }
                    if (result is ApiResult.Problem && result.status < 500) {
                        if (current()) mutable.update { it.copy(phase = GuardianLogPhase.Failed) }
                        return@launch
                    }
                    if (result !is ApiResult.Success) error("Guardian subscription unavailable")
                    disconnected.await()
                } catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { /* The next attempt reopens only this read subscription. */ }
                finally { val old = connection; if (transport === old) transport = null; dispose(old) }
                if (!current()) return@launch
                mutable.update { it.copy(phase = GuardianLogPhase.Connecting) }
            }
            if (current()) mutable.update { it.copy(phase = GuardianLogPhase.Failed) }
        }
    }
    fun stop() {
        generation++; job?.cancel(); job = null; owner = null; selected = null; transport = null
        mutable.value = GuardianLogState()
    }
    private suspend fun dispose(connection: GuardianLogConnection?) {
        withContext(NonCancellable) { try { connection?.disconnect() } catch (_: Exception) { } }
    }
}

/** The hub emits complete snapshots. Replace them rather than appending duplicate reconnect output. */
internal fun boundedGuardianLogs(logs: List<GuardianLog>): Pair<List<GuardianLog>, Boolean> {
    var budget = 65_536; var clipped = logs.size > 200
    val kept = ArrayList<GuardianLog>()
    for (log in logs.takeLast(200).asReversed()) {
        require(log.timestamp.length <= 128 && log.stream.length <= 128)
        if (budget <= 0) { clipped = true; break }
        val limit = minOf(8192, budget)
        var end = minOf(log.message.length, limit)
        if (end < log.message.length && end > 0 && log.message[end - 1].isHighSurrogate()) end--
        clipped = clipped || end < log.message.length
        kept += log.copy(message = log.message.take(end)); budget -= end
    }
    return kept.asReversed() to clipped
}
