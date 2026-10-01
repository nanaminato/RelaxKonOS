package app.relaxkonos.mobile.ui.manage.guardian

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import com.microsoft.signalr.HttpRequestException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class GuardianLogObserverTest {
    private class Connection(val snapshot: (List<GuardianLog>) -> Unit, val closed: () -> Unit) : GuardianLogConnection {
        var failure: Exception? = null; var pending: CompletableDeferred<Unit>? = null
        var id: String? = null; var stops = 0
        override suspend fun connect() { failure?.let { throw it }; pending?.await() }
        override suspend fun subscribe(id: String): List<GuardianLog> { this.id = id; return listOf(log("initial")) }
        override suspend fun disconnect() { stops++ }
    }
    private val gateway = FakeGateway(); private val auth = AuthSession(gateway)
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        auth.login(ServerConnectionIdentityRules.direct("https://host"), "nana", "pw".toCharArray()) {}
        return auth.state.value as SessionState.Active
    }
    private fun observer(scope: TestScope, connections: MutableList<Connection>, configure: (Connection, Int) -> Unit = { _, _ -> }) =
        GuardianLogObserver(auth, scope, GuardianLogConnectionFactory { _, _, snapshot, closed ->
            Connection(snapshot, closed).also { configure(it, connections.size); connections.add(it) }
        })
    @Test fun `snapshots replace output and stopping disconnects without workload mutations`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        observer.observe(owner, "job"); runCurrent()
        assertEquals("job", connections.single().id); assertEquals(GuardianLogPhase.Live, observer.state.value.phase)
        connections.single().snapshot(listOf(log("new"))); runCurrent()
        connections.single().snapshot(listOf(log("new"))); runCurrent()
        assertEquals(listOf(log("new")), observer.state.value.logs)
        observer.stop(); runCurrent(); assertEquals(1, connections.single().stops)
        assertEquals(GuardianLogPhase.Idle, observer.state.value.phase)
    }
    @Test fun `target and owner switches reject late snapshots and dispose old subscriptions`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        observer.observe(owner, "one"); runCurrent(); observer.observe(owner, "two"); runCurrent()
        connections[0].snapshot(listOf(log("old target"))); connections[0].closed(); runCurrent()
        assertEquals(listOf(log("initial")), observer.state.value.logs); assertEquals("two", connections[1].id)
        val next = login(); observer.observe(next, "three"); runCurrent()
        connections[1].snapshot(listOf(log("old owner"))); runCurrent()
        assertEquals(listOf(log("initial")), observer.state.value.logs)
        observer.stop(); runCurrent(); assertTrue(connections.all { it.stops == 1 })
    }
    @Test fun `disconnect reconnects only the original subscription and replaces its initial snapshot`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); val observer = observer(this, connections)
        observer.observe(owner, "job"); runCurrent(); connections[0].closed(); runCurrent()
        advanceTimeBy(1000); runCurrent()
        assertEquals(2, connections.size); assertEquals("job", connections[1].id)
        assertEquals(listOf(log("initial")), observer.state.value.logs)
        observer.stop(); runCurrent()
    }
    @Test fun `retry budget ends with visible failure and an explicit retry can reconnect`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { connection, index -> if (index < 4) connection.failure = Exception("offline") }
        observer.observe(owner, "job"); advanceUntilIdle()
        assertEquals(4, connections.size); assertEquals(GuardianLogPhase.Failed, observer.state.value.phase)
        observer.observe(owner, "job"); runCurrent(); assertEquals(GuardianLogPhase.Live, observer.state.value.phase)
        observer.stop(); runCurrent(); assertTrue(connections.all { it.stops == 1 })
    }
    @Test fun `expired negotiate refreshes authentication without accepting old connection callbacks`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); var refreshes = 0
        gateway.onRefresh = { _, _ -> refreshes++; ApiResult.Success(AuthTokens("new", "new-refresh", null, null)) }
        val observer = observer(this, connections) { connection, index -> if (index == 0) connection.failure = HttpRequestException("negotiate", 401) }
        observer.observe(owner, "job"); runCurrent()
        assertEquals(1, refreshes); assertEquals(2, connections.size); assertEquals(GuardianLogPhase.Live, observer.state.value.phase)
        connections[0].snapshot(listOf(log("old token"))); connections[0].closed(); runCurrent()
        assertEquals(listOf(log("initial")), observer.state.value.logs)
        observer.stop(); runCurrent()
    }
    @Test fun `leaving during connect cancels pending work and closes the partial connection`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>()
        val observer = observer(this, connections) { connection, _ -> connection.pending = CompletableDeferred() }
        observer.observe(owner, "job"); runCurrent(); observer.stop(); runCurrent()
        assertEquals(1, connections.single().stops); assertNull(connections.single().id)
    }
    @Test fun `a repeated unauthorized response stops after a single authentication retry`() = runTest {
        val owner = login(); val connections = mutableListOf<Connection>(); var refreshes = 0
        gateway.onRefresh = { _, _ -> refreshes++; ApiResult.Success(AuthTokens("new", "new-refresh", null, null)) }
        val observer = observer(this, connections) { connection, _ -> connection.failure = HttpRequestException("negotiate", 401) }
        observer.observe(owner, "job"); advanceUntilIdle()
        assertEquals(1, refreshes); assertEquals(2, connections.size)
        assertEquals(GuardianLogPhase.Failed, observer.state.value.phase); assertTrue(connections.all { it.stops == 1 })
        observer.stop()
    }
    @Test fun `bounded snapshots retain recent entries and never split a surrogate pair`() {
        val many = (0..250).map { log(it.toString()) }
        val (values, clipped) = boundedGuardianLogs(many)
        assertEquals(200, values.size); assertEquals("51", values.first().message); assertTrue(clipped)
        val text = "a".repeat(8191) + "🐱tail"
        val bounded = boundedGuardianLogs(List(20) { log(text) })
        assertTrue(bounded.second); assertTrue(bounded.first.sumOf { it.message.length } <= 65536)
        assertTrue(bounded.first.all { it.message.isEmpty() || !it.message.last().isHighSurrogate() })
    }
    companion object { private fun log(message: String) = GuardianLog("2026-10-01T00:00:00Z", "stdout", message) }
}
