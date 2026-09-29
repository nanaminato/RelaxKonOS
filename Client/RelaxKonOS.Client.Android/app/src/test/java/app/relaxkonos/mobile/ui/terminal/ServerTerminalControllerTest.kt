package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.TerminalAttachment
import app.relaxkonos.mobile.core.net.TerminalConnection
import app.relaxkonos.mobile.core.net.TerminalSessionSummary
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import com.microsoft.signalr.HttpRequestException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.cancel
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ServerTerminalControllerTest {
    private class FakeConnection(val closed: () -> Unit, val output: (ByteArray) -> Unit, val exited: (Int) -> Unit) : TerminalConnection {
        var listed = listOf(summary("first"), summary("second"))
        var startFailure: Exception? = null
        var attachGate: CompletableDeferred<Unit>? = null
        val attached = mutableListOf<String?>()
        val inputs = mutableListOf<String>()
        var inputGate: CompletableDeferred<Unit>? = null
        var stops = 0
        var closeGate: CompletableDeferred<Unit>? = null
        override suspend fun connect() { startFailure?.let { throw it } }
        override suspend fun sessions() = listed
        override suspend fun attach(sessionId: String?, columns: Int, rows: Int): TerminalAttachment {
            attached += sessionId
            attachGate?.await()
            val id = sessionId ?: "new"
            if (sessionId == null) listed = listed + summary(id)
            output("prompt $id".toByteArray())
            return TerminalAttachment().also { it.sessionId = id }
        }
        override suspend fun input(bytes: ByteArray) { inputs += bytes.toString(Charsets.UTF_8); inputGate?.await() }
        override suspend fun resize(columns: Int, rows: Int) {}
        override suspend fun closeSession(sessionId: String) { closeGate?.await(); listed = listed.filterNot { it.sessionId == sessionId } }
        override suspend fun disconnect() { stops++; closed() }
    }

    private class Harness(val controller: ServerTerminalController, val auth: AuthSession, val gateway: FakeGateway,
        val owner: SessionState.Active, val connections: MutableList<FakeConnection>, val tokens: MutableList<String>)

    private suspend fun TestScope.harness(configure: (FakeConnection, Int) -> Unit = { _, _ -> }): Harness {
        val gateway = FakeGateway()
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("access-2", "refresh-2", null, null)) }
        val auth = AuthSession(gateway)
        auth.login(ServerConnectionIdentityRules.direct("https://host:5090"), "nana", "pw".toCharArray()) {}
        val connections = mutableListOf<FakeConnection>()
        val tokens = mutableListOf<String>()
        val controller = ServerTerminalController(auth, backgroundScope, TerminalConnectionFactory { _, token, output, exit, closed ->
            FakeConnection(closed, output, exit).also { configure(it, connections.size); connections += it; tokens += token }
        })
        return Harness(controller, auth, gateway, auth.state.value as SessionState.Active, connections, tokens)
    }

    @Test fun `first entry attaches first live session and replays output without starting shell`() = runTest {
        val h = harness { connection, _ -> connection.listed = listOf(summary("exited", true), summary("first"), summary("second")) }
        h.controller.connect(h.owner); runCurrent()
        assertEquals(listOf("first"), h.connections.single().attached)
        assertEquals("first", h.controller.state.value.sessionId)
        assertTrue(h.controller.state.value.output.contains("prompt first"))
        assertTrue(h.controller.state.value.canInput)
    }

    @Test fun `empty list leaves a connected empty state until explicit new session`() = runTest {
        val h = harness { c, _ -> c.listed = emptyList() }
        h.controller.connect(h.owner); runCurrent()
        assertTrue(h.controller.state.value.connected)
        assertFalse(h.controller.state.value.canInput)
        assertTrue(h.connections.single().attached.isEmpty())
        h.controller.attach(null); runCurrent()
        assertEquals("new", h.controller.state.value.sessionId)
    }

    @Test fun `401 negotiate renews token once and rebuilds authenticated transport`() = runTest {
        val h = harness { c, index -> if (index == 0) c.startFailure = HttpRequestException("negotiate", 401) }
        h.controller.connect(h.owner); runCurrent()
        assertEquals(listOf("access-1", "access-2"), h.tokens)
        assertEquals(1, h.gateway.refreshCount)
        assertTrue(h.controller.state.value.connected)
        assertEquals(listOf("first"), h.connections.last().attached)
    }

    @Test fun `a repeated 401 stops after one refresh and one retry`() = runTest {
        val h = harness { c, _ -> c.startFailure = HttpRequestException("negotiate", 401) }
        h.controller.connect(h.owner); runCurrent()
        assertEquals(2, h.connections.size)
        assertEquals(1, h.gateway.refreshCount)
        assertTrue(h.controller.state.value.error)
        assertFalse(h.controller.state.value.connecting)
    }

    @Test fun `403 is a refusal and does not renew or repeatedly reconnect`() = runTest {
        val h = harness { c, _ -> c.startFailure = RuntimeException(HttpRequestException("forbidden", 403)) }
        h.controller.connect(h.owner); runCurrent()
        assertEquals(1, h.connections.size)
        assertEquals(0, h.gateway.refreshCount)
        assertTrue(h.controller.state.value.error)
        assertTrue(h.auth.state.value is SessionState.Active)
    }

    @Test fun `temporary 503 recovers with backoff without refreshing credentials`() = runTest {
        val h = harness { c, index -> if (index < 2) c.startFailure = HttpRequestException("unavailable", 503) }
        h.controller.connect(h.owner); runCurrent()
        advanceTimeBy(1_000); runCurrent()
        advanceTimeBy(2_000); runCurrent()
        assertEquals(3, h.connections.size)
        assertEquals(0, h.gateway.refreshCount)
        assertTrue(h.controller.state.value.connected)
        assertEquals("first", h.controller.state.value.sessionId)
    }

    @Test fun `rejected refresh signs out instead of retrying invalid authentication`() = runTest {
        val h = harness { c, _ -> c.startFailure = HttpRequestException("negotiate", 401) }
        h.gateway.onRefresh = { _, _ -> ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        h.controller.connect(h.owner); runCurrent()
        assertEquals(SessionState.SignedOut, h.auth.state.value)
        assertEquals(1, h.connections.size)
    }

    @Test fun `disconnect restores selected session and does not replay command input`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.attach("second"); runCurrent()
        assertTrue(h.controller.send("echo hello\r")); runCurrent()
        h.connections.first().closed(); runCurrent()
        assertEquals("second", h.controller.state.value.sessionId)
        assertEquals(listOf("second"), h.connections.last().attached)
        assertTrue(h.connections.last().inputs.isEmpty())
        assertEquals(listOf("echo hello\r"), h.connections.first().inputs)
        assertFalse(h.connections.flatMap { it.attached }.contains(null))
    }

    @Test fun `disconnect during initial attachment cannot leave a dead connection marked ready`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val h = harness { c, index -> if (index == 0) c.attachGate = gate }
        h.controller.connect(h.owner); runCurrent()
        h.connections.single().closed(); runCurrent()
        gate.complete(Unit); runCurrent()
        assertFalse(h.controller.state.value.connected)
        advanceTimeBy(1_000); runCurrent()
        assertEquals(2, h.connections.size)
        assertTrue(h.controller.state.value.canInput)
        assertEquals(listOf("first"), h.connections.last().attached)
    }

    @Test fun `lost saved session is reported without attaching a different shell`() = runTest {
        val h = harness { c, index -> if (index > 0) c.listed = listOf(summary("second")) }
        h.controller.connect(h.owner); runCurrent()
        h.connections.first().closed(); runCurrent()
        assertTrue(h.controller.state.value.sessionLost)
        assertNull(h.controller.state.value.sessionId)
        assertTrue(h.connections.last().attached.isEmpty())
        h.connections.last().closed(); runCurrent()
        assertTrue(h.controller.state.value.sessionLost)
        assertTrue(h.connections.last().attached.isEmpty())
    }

    @Test fun `closing current session selects remaining first session`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.close("first"); runCurrent()
        assertEquals("second", h.controller.state.value.sessionId)
        assertEquals(listOf("first", "second"), h.connections.single().attached)
    }

    @Test fun `session exit disables input but keeps hub ready for another session`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.connections.single().exited(0); runCurrent()
        assertTrue(h.controller.state.value.connected)
        assertEquals(0, h.controller.state.value.exitCode)
        assertFalse(h.controller.send("dangerous\r"))
        h.controller.attach("second"); runCurrent()
        assertTrue(h.controller.state.value.canInput)
    }

    @Test fun `switching blocks duplicate creation and input until attachment completes`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val gate = CompletableDeferred<Unit>()
        h.connections.single().attachGate = gate
        h.controller.attach(null); runCurrent()
        h.controller.attach(null)
        assertFalse(h.controller.send("wrong session\r"))
        assertTrue(h.controller.state.value.busy)
        gate.complete(Unit); runCurrent()
        assertEquals(listOf("first", null), h.connections.single().attached)
        assertTrue(h.controller.state.value.canInput)
    }

    @Test fun `session switch waits for an in flight write to finish`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val gate = CompletableDeferred<Unit>()
        h.connections.single().inputGate = gate
        assertTrue(h.controller.send("old shell command\r")); runCurrent()
        h.controller.attach("second"); runCurrent()
        assertEquals(listOf("first"), h.connections.single().attached)
        assertTrue(h.controller.state.value.busy)
        gate.complete(Unit); runCurrent()
        assertEquals(listOf("first", "second"), h.connections.single().attached)
        assertEquals("second", h.controller.state.value.sessionId)
    }

    @Test fun `returning from background reattaches the selected session`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.attach("second"); runCurrent()
        h.controller.detach(); runCurrent()
        assertFalse(h.controller.state.value.connected)
        h.controller.connect(h.owner); runCurrent()
        assertEquals(listOf("second"), h.connections.last().attached)
        assertTrue(h.controller.state.value.canInput)
    }

    @Test fun `detach stops transport even after its UI scope has been cancelled`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        backgroundScope.cancel()
        h.controller.detach(); runCurrent()
        assertEquals(1, h.connections.single().stops)
        assertFalse(h.controller.state.value.connected)
    }

    @Test fun `detach cancels pending attachment and ignores late transport callbacks`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val old = h.connections.single()
        old.attachGate = CompletableDeferred()
        h.controller.attach("second"); runCurrent()
        h.controller.detach(); runCurrent()
        old.output("stale".toByteArray()); old.exited(9); old.closed(); runCurrent()
        assertFalse(h.controller.state.value.connected)
        assertFalse(h.controller.state.value.busy)
        assertFalse(h.controller.state.value.output.contains("stale"))
        assertNull(h.controller.state.value.exitCode)
        assertEquals(1, h.connections.size)
    }

    @Test fun `network recovery is bounded and manual retry remains available`() = runTest {
        val h = harness { c, _ -> c.startFailure = IllegalStateException("offline") }
        h.controller.connect(h.owner); runCurrent()
        repeat(3) { advanceTimeBy(5_000); runCurrent() }
        assertEquals(4, h.connections.size)
        assertTrue(h.controller.state.value.error)
        assertFalse(h.controller.state.value.connecting)
        assertTrue(h.auth.state.value is SessionState.Active)
        h.controller.connect(h.owner); runCurrent()
        assertEquals(5, h.connections.size)
        h.controller.detach(); runCurrent()
    }

    companion object {
        private fun summary(id: String, exited: Boolean = false) = TerminalSessionSummary().also { it.sessionId = id; it.hasExited = exited }
    }
}
