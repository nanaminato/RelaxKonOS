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
        val attachedSizes = mutableListOf<Pair<Int, Int>>()
        val inputs = mutableListOf<String>()
        val sizes = mutableListOf<Pair<Int, Int>>()
        var onResize: ((Int, Int) -> Unit)? = null
        var resizeGate: CompletableDeferred<Unit>? = null
        var activeResizes = 0
        var maximumActiveResizes = 0
        var inputGate: CompletableDeferred<Unit>? = null
        var stops = 0
        var closeGate: CompletableDeferred<Unit>? = null
        override suspend fun connect() { startFailure?.let { throw it } }
        override suspend fun sessions() = listed
        override suspend fun attach(sessionId: String?, columns: Int, rows: Int): TerminalAttachment {
            attached += sessionId
            attachedSizes += columns to rows
            attachGate?.await()
            val id = sessionId ?: "new"
            if (sessionId == null) listed = listed + summary(id)
            output("prompt $id".toByteArray())
            return TerminalAttachment().also { it.sessionId = id }
        }
        override suspend fun input(bytes: ByteArray) { inputs += bytes.toString(Charsets.UTF_8); inputGate?.await() }
        override suspend fun resize(columns: Int, rows: Int) {
            activeResizes++
            maximumActiveResizes = maxOf(maximumActiveResizes, activeResizes)
            try { sizes += columns to rows; onResize?.invoke(columns, rows); resizeGate?.await() }
            finally { activeResizes-- }
        }
        override suspend fun closeSession(sessionId: String) { closeGate?.await(); listed = listed.filterNot { it.sessionId == sessionId } }
        override suspend fun disconnect() { stops++; closed() }
    }

    private class Harness(val controller: ServerTerminalController, val auth: AuthSession, val gateway: FakeGateway,
        val owner: SessionState.Active, val connections: MutableList<FakeConnection>, val tokens: MutableList<String>)

    private suspend fun TestScope.harness(nativeResponses: () -> Boolean = { true }, configure: (FakeConnection, Int) -> Unit = { _, _ -> }): Harness {
        val gateway = FakeGateway()
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("access-2", "refresh-2", null, null)) }
        val auth = AuthSession(gateway)
        auth.login(ServerConnectionIdentityRules.direct("https://host:5090"), "nana", "pw".toCharArray()) {}
        val connections = mutableListOf<FakeConnection>()
        val tokens = mutableListOf<String>()
        val controller = ServerTerminalController(auth, backgroundScope, TerminalConnectionFactory { _, token, output, exit, closed ->
            FakeConnection(closed, output, exit).also { configure(it, connections.size); connections += it; tokens += token }
        }, nativeResponses = nativeResponses)
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

    @Test fun `both renderer outputs reset together when attaching another shell`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        val vt = "\u001b[2J\u001b[H\u001b[32m中>\u001b[0m"
        connection.output(vt.toByteArray()); runCurrent()
        assertEquals("中>", h.controller.state.value.output)
        assertEquals("prompt first" + vt, h.controller.state.value.rawOutput)
        h.controller.attach("second"); runCurrent()
        assertEquals("prompt second", h.controller.state.value.output)
        assertEquals("prompt second", h.controller.state.value.rawOutput)
        assertEquals(1, h.connections.size)
    }

    @Test fun `renderer switching keeps one PTY and only the active renderer answers queries`() = runTest {
        var native = false
        val h = harness(nativeResponses = { native })
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        connection.output("\u001b[6n".toByteArray()); runCurrent()
        assertTrue(connection.inputs.isEmpty())
        assertTrue(h.controller.state.value.rawOutput.endsWith("\u001b[6n"))
        native = true
        connection.output("\u001b[6n".toByteArray()); runCurrent()
        assertEquals(listOf("\u001b[1;13R"), connection.inputs)
        assertEquals(listOf("first"), connection.attached)
        assertEquals(1, h.connections.size)
        h.controller.clearOutput()
        assertEquals("", h.controller.state.value.rawOutput)
        connection.output("\r\nnext".toByteArray()); runCurrent()
        assertEquals("\r\nnext", h.controller.state.value.rawOutput)
        h.controller.detach(); runCurrent()
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

    @Test fun `resize waits for layout to settle before changing the parser grid`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        connection.output("\u001b[2J\u001b[H1234567890123456789012345".toByteArray()); runCurrent()
        h.controller.resize(20, 5)
        runCurrent()
        assertEquals("1234567890123456789012345", h.controller.state.value.output)
        assertTrue(connection.sizes.isEmpty())
        advanceTimeBy(200); runCurrent()
        assertEquals("12345678901234567890", h.controller.state.value.output)
        assertEquals(listOf(20 to 5), connection.sizes)
        connection.output("\u001b[HPS test>\u001b[J".toByteArray()); runCurrent()
        assertEquals("PS test>", h.controller.state.value.output)
    }

    @Test fun `keyboard animation sends only the final size and never submits input`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        for (height in listOf(28, 26, 24, 20, 16, 12, 10)) {
            h.controller.resize(48, height); runCurrent()
            advanceTimeBy(30); runCurrent()
        }
        assertTrue(connection.sizes.isEmpty())
        advanceTimeBy(400); runCurrent()
        assertEquals(listOf(48 to 10), connection.sizes)
        assertTrue(connection.inputs.isEmpty())
    }

    @Test fun `old Windows repaint during keyboard animation does not become duplicate history`() = runTest {
        val h = harness()
        h.controller.resize(48, 18)
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        fun repaint(height: Int) = "\u001b[?25l\u001b[HPS test>\u001b[K\r\n" +
            "\u001b[K\r\n".repeat(height - 2) + "\u001b[K\u001b[H\u001b[?25h"
        connection.output(("\u001b[2J" + repaint(18)).toByteArray()); runCurrent()
        connection.onResize = { _, height -> connection.output(repaint(height).toByteArray()) }
        for (height in listOf(16, 14, 12, 10, 7)) {
            h.controller.resize(48, height); runCurrent()
            connection.output(repaint(18).toByteArray()); runCurrent()
            advanceTimeBy(30); runCurrent()
        }
        advanceTimeBy(400); runCurrent()
        assertEquals("PS test>", h.controller.state.value.output)
        assertEquals(listOf(48 to 7), connection.sizes)
        assertTrue(connection.inputs.isEmpty())
    }

    @Test fun `layout returning to the negotiated size skips resize entirely`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.resize(48, 10); runCurrent()
        advanceTimeBy(50); runCurrent()
        h.controller.resize(80, 24); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertTrue(h.connections.single().sizes.isEmpty())
        assertEquals("prompt first", h.controller.state.value.output)
    }

    @Test fun `resizes are serialized and a newer layout waits for the in flight call`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single()
        val gate = CompletableDeferred<Unit>()
        connection.resizeGate = gate
        h.controller.resize(48, 12); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertEquals(listOf(48 to 12), connection.sizes)
        h.controller.resize(48, 10); runCurrent()
        h.controller.resize(48, 7); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertEquals(listOf(48 to 12), connection.sizes)
        connection.resizeGate = null
        gate.complete(Unit); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertEquals(listOf(48 to 12, 48 to 7), connection.sizes)
        assertEquals(1, connection.maximumActiveResizes)
    }

    @Test fun `detaching cancels pending resize instead of sending it to a stale transport`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.resize(48, 7); runCurrent()
        h.controller.detach(); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertTrue(h.connections.single().sizes.isEmpty())
    }

    @Test fun `switching sessions uses latest layout and drops the old pending resize`() = runTest {
        val h = harness()
        h.controller.connect(h.owner); runCurrent()
        h.controller.resize(48, 7); runCurrent()
        h.controller.attach("second"); runCurrent()
        advanceTimeBy(400); runCurrent()
        val connection = h.connections.single()
        assertEquals(listOf(80 to 24, 48 to 7), connection.attachedSizes)
        assertTrue(connection.sizes.isEmpty())
        assertEquals("prompt second", h.controller.state.value.output)
    }

    @Test fun `layout changing during attachment is synchronized once after attachment completes`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val h = harness { connection, _ -> connection.attachGate = gate }
        h.controller.connect(h.owner); runCurrent()
        h.controller.resize(48, 12); runCurrent()
        h.controller.resize(48, 7); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertTrue(h.connections.single().sizes.isEmpty())
        gate.complete(Unit); runCurrent()
        advanceTimeBy(400); runCurrent()
        assertEquals(listOf(48 to 7), h.connections.single().sizes)
        assertTrue(h.controller.state.value.canInput)
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

    @Test fun `bulk close only targets confirmed ids despite newly discovered sessions`() = runTest {
        val h = harness(); h.controller.connect(h.owner); runCurrent()
        val connection = h.connections.single(); connection.listed += summary("unconfirmed")
        h.controller.closeSessions(listOf("second")); runCurrent()
        assertEquals(listOf("first", "unconfirmed"), connection.listed.map { it.sessionId })
        assertEquals("first", h.controller.state.value.sessionId); h.controller.detach(); runCurrent()
    }
    @Test fun `local clear does not send input and leaves parser cursor coordinates intact`() = runTest {
        val h = harness(); h.controller.connect(h.owner); runCurrent(); val connection = h.connections.single()
        h.controller.clearOutput(); assertEquals("", h.controller.state.value.output); assertTrue(connection.inputs.isEmpty())
        connection.output("\r\nnext".toByteArray()); runCurrent(); assertTrue(h.controller.state.value.output.contains("next"))
        assertEquals(1, connection.attached.size); h.controller.detach(); runCurrent()
    }
    @Test fun `explicit owner reset discards transcript and selected session without killing remote PTY`() = runTest {
        val h = harness(); h.controller.connect(h.owner); runCurrent(); h.controller.attach("second"); runCurrent()
        h.controller.resetOwner(); runCurrent()
        assertEquals(ServerTerminalState(), h.controller.state.value); assertEquals(1, h.connections.first().stops)
        assertEquals(2, h.connections.first().listed.size)
        h.controller.connect(h.owner); runCurrent(); assertEquals(listOf("first"), h.connections.last().attached)
        h.controller.detach(); runCurrent()
    }


    @Test fun `current terminal device queries receive replies but detached callbacks cannot send`() = runTest {
        val h = harness(); h.controller.connect(h.owner); runCurrent(); val connection = h.connections.single()
        connection.output("\u001b[2J\u001b[H中\u001b[6n".toByteArray()); runCurrent()
        assertEquals("\u001b[1;3R", connection.inputs.single())
        assertEquals("中 ", h.controller.state.value.frame.text)
        h.controller.detach(); runCurrent()
        connection.output("\u001b[6n".toByteArray()); runCurrent()
        assertEquals(1, connection.inputs.size)
    }

    companion object {
        private fun summary(id: String, exited: Boolean = false) = TerminalSessionSummary().also { it.sessionId = id; it.hasExited = exited }
    }
}
