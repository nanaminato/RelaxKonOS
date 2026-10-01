package app.relaxkonos.mobile.ui.manage.monitor

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import com.microsoft.signalr.HttpRequestException
import java.time.OffsetDateTime
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class PerformanceObserverTest {
    private class Connection(val snapshot: (PerformanceSnapshot) -> Unit, val closed: () -> Unit) : PerformanceConnection {
        var failure: Exception? = null; var pending: CompletableDeferred<Unit>? = null
        var initial: PerformanceSnapshot? = sample(8); var subscriptions = 0; var stops = 0
        override suspend fun connect() { failure?.let { throw it }; pending?.await() }
        override suspend fun subscribe() { subscriptions++; initial?.let(snapshot) }
        override suspend fun disconnect() { stops++ }
    }
    private val gateway = FakeGateway(); private val auth = AuthSession(gateway)
    private var restSequence = 7L
    private var reads = 0
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        gateway.onPerformanceInfo = { _, _ -> ApiResult.Success(PerformanceWire.info(PerformanceWireTest.INFO)) }
        gateway.onNetworkAddresses = { _, _ -> ApiResult.Success(emptyList()) }
        gateway.onPerformance = { _, _ -> reads++; ApiResult.Success(sample(restSequence)) }
        gateway.onPerformanceHistory = { _, _ -> ApiResult.Success(listOf(sample(restSequence - 1))) }
        auth.login(ServerConnectionIdentityRules.direct("https://host"), "nana", "pw".toCharArray()) {}
        return auth.state.value as SessionState.Active
    }
    private fun observer(scope: TestScope, values: MutableList<Connection>, configure: (Connection, Int) -> Unit = { _, _ -> }) =
        PerformanceObserver(auth, SystemRepository(gateway, auth), scope, PerformanceConnectionFactory { _, _, snapshot, closed ->
            Connection(snapshot, closed).also { configure(it, values.size); values.add(it) }
        })
    @Test fun `current stream merges server history and rejects duplicated or older frames`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        observer.observe(owner); runCurrent()
        assertEquals(PerformancePhase.Live, observer.state.value.phase)
        assertEquals(listOf(6L, 7L, 8L), observer.state.value.history.map { it.sequence })
        connections.single().snapshot(sample(8)); connections.single().snapshot(sample(6)); runCurrent()
        assertEquals(8L, observer.state.value.snapshot!!.sequence)
        connections.single().snapshot(sample(9)); runCurrent()
        assertEquals(9L, observer.state.value.snapshot!!.sequence)
        observer.stop(); runCurrent(); assertEquals(1, connections.single().stops)
    }
    @Test fun `reconnection accepts a restarted server and ignores the old transport`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { value, index -> if (index > 0) value.initial = sample(2) }
        observer.observe(owner); runCurrent(); restSequence = 1
        connections[0].closed(); runCurrent(); advanceTimeBy(1_000); runCurrent()
        assertEquals(2, connections.size); assertEquals(2L, observer.state.value.snapshot!!.sequence)
        assertEquals(listOf(0L, 1L, 2L), observer.state.value.history.map { it.sequence })
        connections[0].snapshot(sample(100)); connections[0].closed(); runCurrent()
        assertEquals(2L, observer.state.value.snapshot!!.sequence)
        observer.stop(); runCurrent(); assertTrue(connections.all { it.stops == 1 })
    }
    @Test fun `a new owner clears selection facts and rejects old callbacks`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        observer.observe(owner); runCurrent(); val next = login(); restSequence = 1
        observer.observe(next); runCurrent(); connections[0].snapshot(sample(100)); runCurrent()
        assertEquals(8L, observer.state.value.snapshot!!.sequence)
        observer.stop(); runCurrent(); connections.last().snapshot(sample(200)); runCurrent()
        assertEquals(PerformanceState(), observer.state.value)
        assertTrue(connections.all { it.stops == 1 })
    }
    @Test fun `one authentication refresh reopens a read subscription and rejects the expired callbacks`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); var renewals = 0
        gateway.onRefresh = { _, _ -> renewals++; ApiResult.Success(AuthTokens("new", "next", null, null)) }
        val observer = observer(this, connections) { value, index -> if (index == 0) value.failure = HttpRequestException("negotiate", 401) }
        observer.observe(owner); runCurrent()
        assertEquals(1, renewals); assertEquals(2, connections.size); assertEquals(PerformancePhase.Live, observer.state.value.phase)
        connections[0].snapshot(sample(100)); connections[0].closed(); runCurrent()
        assertEquals(8L, observer.state.value.snapshot!!.sequence)
        observer.stop(); runCurrent()
    }
    @Test fun `forbidden stream is a visible terminal refusal rather than unlimited retries`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { value, _ -> value.failure = HttpRequestException("forbidden", 403) }
        observer.observe(owner); runCurrent(); advanceTimeBy(60_000); runCurrent()
        assertEquals(1, connections.size); assertEquals(0, reads)
        assertEquals(PerformancePhase.Failed, observer.state.value.phase)
        observer.stop(); runCurrent(); assertEquals(1, connections.single().stops)
    }
    @Test fun `bounded stream retries degrade to labelled foreground snapshots and stop all reads when leaving`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { value, _ -> value.failure = Exception("offline") }
        observer.observe(owner); runCurrent(); advanceTimeBy(8_001); runCurrent()
        assertEquals(4, connections.size); assertEquals(PerformancePhase.Snapshot, observer.state.value.phase)
        val before = reads; restSequence = 9; advanceTimeBy(5_000); runCurrent()
        assertEquals(before + 1, reads); assertEquals(9L, observer.state.value.snapshot!!.sequence)
        assertTrue(observer.state.value.history.any { it.sequence == 7L })
        observer.stop(); runCurrent(); val stopped = reads; advanceTimeBy(60_000); runCurrent()
        assertEquals(stopped, reads); assertTrue(connections.all { it.stops == 1 })
    }
    @Test fun `a silent subscribed transport is not displayed as permanently live`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { value, _ -> value.initial = null }
        observer.observe(owner); runCurrent(); advanceTimeBy(5_001); runCurrent()
        assertEquals(PerformancePhase.Snapshot, observer.state.value.phase)
        assertEquals(1, connections[0].stops)
        observer.stop(); runCurrent()
    }
    @Test fun `leaving during a pending handshake cancels and disposes the partial connection`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { value, _ -> value.pending = CompletableDeferred() }
        observer.observe(owner); runCurrent(); observer.stop(); runCurrent()
        assertEquals(0, connections.single().subscriptions); assertEquals(1, connections.single().stops)
        assertEquals(PerformanceState(), observer.state.value)
    }
    @Test fun `owner change during initial REST cannot publish facts from the old login`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        gateway.onPerformanceInfo = { _, _ -> login(); ApiResult.Success(PerformanceWire.info(PerformanceWireTest.INFO)) }
        observer.observe(owner); runCurrent()
        assertNull(observer.state.value.info); assertNull(observer.state.value.snapshot)
        assertEquals(1, connections.single().stops)
        observer.stop()
    }
    @Test fun `history retains actual gaps and bounds both seconds and point count`() {
        val samples = (0L..100L).map { sample(it) }
        val merged = mergePerformanceHistory(samples, listOf(sample(100)))
        assertEquals(60, merged.size); assertEquals(41L, merged.first().sequence)
        val gap = mergePerformanceHistory(listOf(sample(1), sample(3)), listOf(sample(20)))
        assertEquals(listOf(1L, 3L, 20L), gap.map { it.sequence })
        assertEquals(listOf(100L), mergePerformanceHistory(listOf(sample(1)), listOf(sample(100))).map { it.sequence })
    }
    companion object {
        private fun sample(sequence: Long): PerformanceSnapshot = PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT).copy(sequence = sequence,
            timestamp = OffsetDateTime.parse("2026-10-01T00:00:00Z").plusSeconds(sequence).toInstant().toString(), health = PerformanceHealth(false, null, null))
    }
}
