package app.relaxkonos.mobile.ui.manage.monitor

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.selects.onTimeout
import kotlinx.coroutines.selects.select

internal enum class PerformancePhase { Idle, Connecting, Live, Snapshot, Failed }
internal data class PerformanceState(
    val phase: PerformancePhase = PerformancePhase.Idle,
    val info: PerformanceInfo? = null,
    val snapshot: PerformanceSnapshot? = null,
    val history: List<PerformanceSnapshot> = emptyList(),
    val addresses: List<NetworkAddress> = emptyList(),
    val problem: ApiResult<Nothing>? = null,
)
internal fun interface PerformanceConnectionFactory {
    fun create(url: String, token: String, snapshot: (PerformanceSnapshot) -> Unit, closed: () -> Unit): PerformanceConnection
}

/** A foreground-only read observer. Each transport gets a fresh sequence baseline, including a server restart. */
@OptIn(ExperimentalCoroutinesApi::class)
internal class PerformanceObserver(private val auth: AuthSession, private val repository: SystemRepository,
    private val scope: CoroutineScope,
    private val factory: PerformanceConnectionFactory = PerformanceConnectionFactory { url, token, snapshot, closed ->
        ServerPerformanceConnection(url, token, snapshot, closed)
    }) {
    private val mutable = MutableStateFlow(PerformanceState())
    val state = mutable.asStateFlow()
    @Volatile private var generation = 0
    @Volatile private var transport: PerformanceConnection? = null
    private var job: Job? = null
    private var owner: SessionState.Active? = null

    fun observe(active: SessionState.Active) {
        if (owner === active && job?.isActive == true) return
        stop(); owner = active
        if (auth.state.value !== active) return
        val version = generation
        fun current() = version == generation && auth.state.value === active
        fun guard() { if (!current()) throw CancellationException("Performance owner changed") }
        mutable.value = PerformanceState(PerformancePhase.Connecting)
        job = scope.launch {
            for (wait in listOf(0L, 1_000L, 2_000L, 5_000L)) {
                delay(wait); guard()
                mutable.update { it.copy(phase = PerformancePhase.Connecting) }
                var connection: PerformanceConnection? = null
                var closed = CompletableDeferred<Unit>()
                var events = Channel<PerformanceSnapshot>(Channel.CONFLATED)
                try {
                    val token = auth.connectionToken(); guard()
                    if (token !is ApiResult.Success) {
                        if (failure(token) is ApiResult.Problem) { mutable.update { it.copy(phase = PerformancePhase.Failed, problem = failure(token)) }; return@launch }
                        error("Performance authentication unavailable")
                    }
                    val subscribed = auth.authenticated { url, accessToken ->
                        guard(); require(url == active.effectiveBaseUrl)
                        val attemptEvents = Channel<PerformanceSnapshot>(Channel.CONFLATED)
                        val attemptClosed = CompletableDeferred<Unit>()
                        events.close(); events = attemptEvents; closed = attemptClosed
                        var callback: PerformanceConnection? = null
                        val candidate = factory.create(url, accessToken,
                            { value -> if (callback != null && current() && transport === callback) attemptEvents.trySend(value) },
                            { if (callback != null && current() && transport === callback) attemptClosed.complete(Unit) })
                        callback = candidate; connection = candidate; transport = candidate
                        try {
                            candidate.connect(); guard(); candidate.subscribe(); guard()
                            ApiResult.Success(Unit)
                        } catch (cancelled: CancellationException) { throw cancelled }
                        catch (error: Exception) {
                            if (transport === candidate) transport = null
                            connection = null; dispose(candidate); terminalConnectionFailure(error)
                        }
                    }
                    guard()
                    if (subscribed is ApiResult.Problem && subscribed.status < 500) {
                        mutable.update { it.copy(phase = PerformancePhase.Failed, problem = failure(subscribed)) }; return@launch
                    }
                    if (subscribed !is ApiResult.Success) error("Performance stream unavailable")
                    // The new subscription may be a new Server process. Never compare its sequence to the old connection.
                    mutable.value = PerformanceState(PerformancePhase.Connecting)
                    readFacts(active, ::guard, reset = true)
                    var receivedSinceCheck = false
                    while (current()) {
                        val event = select<StreamEvent> {
                            events.onReceiveCatching { result -> result.getOrNull()?.let { StreamEvent.Sample(it) } ?: StreamEvent.Closed }
                            closed.onAwait { StreamEvent.Closed }
                            onTimeout(5_000) {
                                if (!receivedSinceCheck) StreamEvent.Closed else { receivedSinceCheck = false; StreamEvent.Check }
                            }
                        }
                        if (event === StreamEvent.Closed) break
                        if (event === StreamEvent.Check) continue
                        val value = (event as StreamEvent.Sample).value
                        guard()
                        val last = mutable.value.snapshot
                        if (last == null || value.sequence > last.sequence) {
                            receivedSinceCheck = true
                            mutable.update { it.copy(phase = PerformancePhase.Live, snapshot = value,
                                history = mergePerformanceHistory(it.history, listOf(value))) }
                        }
                    }
                } catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { /* Bounded retries only reopen the read subscription. */ }
                finally {
                    events.close(); val old = connection; if (transport === old) transport = null; dispose(old)
                }
                guard()
                mutable.update { it.copy(phase = PerformancePhase.Connecting) }
                readFacts(active, ::guard, reset = false, fallback = true)
            }
            // A failed stream remains useful as explicitly labelled, foreground-only REST snapshots.
            while (current()) {
                delay(5_000); guard()
                readFacts(active, ::guard, reset = false, fallback = true, full = mutable.value.info == null)
            }
        }
    }

    fun retry(active: SessionState.Active) { stop(); observe(active) }
    fun dismissProblem() { mutable.update { it.copy(problem = null) } }
    fun stop() {
        generation++; job?.cancel(); job = null; owner = null; transport = null
        mutable.value = PerformanceState()
    }
    private suspend fun readFacts(active: SessionState.Active, guard: () -> Unit, reset: Boolean,
        fallback: Boolean = false, full: Boolean = true) {
        var problem: ApiResult<Nothing>? = null
        if (full) {
            val info = repository.performanceInfo(active); guard()
            if (info is ApiResult.Success) mutable.update { it.copy(info = info.value) } else problem = failure(info)
            val addresses = repository.networkAddresses(active); guard()
            if (addresses is ApiResult.Success) mutable.update { it.copy(addresses = addresses.value) } else if (problem == null) problem = failure(addresses)
        }
        val snapshot = repository.performance(active); guard()
        val history = repository.performanceHistory(active); guard()
        if (snapshot !is ApiResult.Success) problem = failure(snapshot)
        else if (history !is ApiResult.Success && problem == null) problem = failure(history)
        val previous = mutable.value.snapshot
        val restarted = snapshot is ApiResult.Success && previous != null && (snapshot.value.sequence < previous.sequence ||
            snapshot.value.sequence == previous.sequence && snapshot.value.timestamp != previous.timestamp)
        val samples = mergePerformanceHistory(if (reset || restarted) emptyList() else mutable.value.history,
            (if (history is ApiResult.Success) history.value else emptyList()) + (if (snapshot is ApiResult.Success) listOf(snapshot.value) else emptyList()))
        mutable.update { it.copy(
            info = if (restarted && !full) null else it.info,
            addresses = if (restarted && !full) emptyList() else it.addresses,
            phase = if (fallback) { if (snapshot is ApiResult.Success) PerformancePhase.Snapshot else PerformancePhase.Failed } else PerformancePhase.Connecting,
            snapshot = samples.lastOrNull() ?: it.snapshot,
            history = samples,
            problem = problem,
        ) }
    }
    private suspend fun dispose(connection: PerformanceConnection?) {
        withContext(NonCancellable) { try { connection?.disconnect() } catch (_: Exception) { } }
    }
    private fun failure(value: ApiResult<*>): ApiResult<Nothing>? = when (value) {
        is ApiResult.Problem -> value
        is ApiResult.Transport -> value
        is ApiResult.Success -> null
    }
    private sealed interface StreamEvent {
        data class Sample(val value: PerformanceSnapshot) : StreamEvent
        data object Check : StreamEvent
        data object Closed : StreamEvent
    }
}

/** Server timestamps define a real 60-second window. Missing seconds remain gaps, never invented points. */
internal fun mergePerformanceHistory(existing: List<PerformanceSnapshot>, incoming: List<PerformanceSnapshot>): List<PerformanceSnapshot> {
    val samples = (existing + incoming).associateBy { it.sequence }.values.sortedBy { it.sequence }
    val newest = samples.lastOrNull() ?: return emptyList()
    val latest = IsoInstant.requireEpochMillis(newest.timestamp)
    return samples.filter { IsoInstant.requireEpochMillis(it.timestamp) in (latest - 60_000)..latest }.takeLast(60)
}
