package app.relaxkonos.mobile.servercenter

import java.io.IOException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class SshTerminalSessionsTest {
    @Test fun `native and xterm views retain output through navigation resize and reconnect`() = runTest {
        val h = Harness(this)
        val id = h.manager.create("host-a")!!; runCurrent()
        val shell = h.shells.single().second
        val chunks = listOf("old\r\n", "\u001b[", "2J\u001b[H", "\u001b[32m中>\u001b[0m")
        for (chunk in chunks) { shell.output.send(chunk); runCurrent() }
        h.manager.updateDraft(id, "unsent")
        h.manager.resize(id, 48, 12); runCurrent()
        h.manager.enter("host-a"); runCurrent()
        val state = h.manager.state.value.selectedForHost("host-a")!!
        assertEquals(chunks.joinToString(""), state.output)
        assertEquals("中>", state.transcript)
        assertEquals("unsent", state.draft)
        assertEquals(1, h.shells.size)
        assertFalse(shell.closed.isCompleted)
        shell.output.close(); runCurrent()
        h.manager.reconnect(id); runCurrent()
        assertEquals("", h.manager.state.value.sessions.single().output)
        assertEquals("", h.manager.state.value.sessions.single().transcript)
        h.shells.last().second.output.send("new>"); runCurrent()
        assertEquals("new>", h.manager.state.value.sessions.single().transcript)
    }

    @Test fun `Windows redraw sequences and split frames reach emulator unchanged`() = runTest {
        val h = Harness(this)
        val id = h.manager.create("host-a")!!; runCurrent()
        val shell = h.shells.single().second
        val chunks = listOf("Windows\r\nC:\\>", "\u001b[", "2J\u001b[H", "\u001b[32mC:\\>\u001b[0m")
        for (chunk in chunks) { shell.output.send(chunk); runCurrent() }
        h.manager.resize(id, 8, 2); runCurrent()
        assertEquals(chunks.joinToString(""), h.manager.state.value.sessions.single().output)
        assertEquals(listOf(8 to 2), shell.sizes)
        assertEquals(1, h.shells.size)
    }
    private class Shell : ServerCenterSshTerminal {
        val output = Channel<String>(Channel.UNLIMITED)
        val input = mutableListOf<String>()
        val sizes = mutableListOf<Pair<Int, Int>>()
        val closed = CompletableDeferred<Unit>()
        var closes = 0
        var failWrite = false
        override suspend fun read() = output.receiveCatching().getOrNull()
        override suspend fun write(value: String) {
            if (failWrite) throw IOException("write failed")
            input += value
        }
        override suspend fun resize(columns: Int, rows: Int) { sizes += columns to rows }
        override fun close() { closes++; output.close(); closed.complete(Unit) }
    }

    private class Harness(scope: TestScope) {
        val shells = mutableListOf<Pair<String, Shell>>()
        val manager = manager(scope)
        fun manager(scope: TestScope) = SshTerminalSessions(scope.backgroundScope, SshTerminalConnector { hostId ->
            val shell = Shell()
            shells += hostId to shell
            SshTerminalConnection(shell, AutoCloseable {})
        }, { it in setOf("host-a", "host-b") }, { 1234 })
    }

    @Test fun `re-entering and selecting never replaces a live shell or its draft`() = runTest {
        val h = Harness(this)
        h.manager.enter("host-a"); runCurrent()
        val session = h.manager.state.value.selectedForHost("host-a")!!
        val shell = h.shells.single().second
        h.manager.updateDraft(session.sessionId, "unsent secret")
        h.manager.concealInput(session.sessionId, true)
        shell.output.send("user:~$ "); runCurrent()
        repeat(4) { h.manager.enter("host-a"); runCurrent() }
        assertEquals(1, h.shells.size)
        assertFalse(shell.closed.isCompleted)
        assertEquals("user:~$ ", h.manager.state.value.sessions.single().output)
        assertEquals("unsent secret", h.manager.state.value.sessions.single().draft)
        assertTrue(h.manager.state.value.sessions.single().concealInput)
    }

    @Test fun `multiple hosts and shells isolate input output resize and selection`() = runTest {
        val h = Harness(this)
        val first = h.manager.create("host-a")!!; runCurrent()
        val second = h.manager.create("host-a")!!; runCurrent()
        val third = h.manager.create("host-b")!!; runCurrent()
        h.manager.updateDraft(first, "draft-a")
        h.manager.updateDraft(second, "draft-b")
        h.manager.send(first, "pwd\r")
        h.manager.send(second, "ls\r")
        h.manager.send(third, "hostname\r")
        h.manager.resize(second, 48, 12)
        h.shells[0].second.output.send("first-output")
        h.shells[1].second.output.send("second-output")
        h.manager.select(first); runCurrent()
        assertEquals(listOf("pwd\r"), h.shells[0].second.input)
        assertEquals(listOf("ls\r"), h.shells[1].second.input)
        assertEquals(listOf("hostname\r"), h.shells[2].second.input)
        assertEquals(listOf(48 to 12), h.shells[1].second.sizes)
        assertEquals("first-output", h.manager.state.value.selectedForHost("host-a")!!.output)
        assertEquals("draft-a", h.manager.state.value.selectedForHost("host-a")!!.draft)
        h.manager.select(second)
        assertEquals("draft-b", h.manager.state.value.selectedForHost("host-a")!!.draft)
        assertEquals(third, h.manager.state.value.selectedForHost("host-b")!!.sessionId)
    }

    @Test fun `ending one shell releases it and leaves the other selected and writable`() = runTest {
        val h = Harness(this)
        val first = h.manager.create("host-a")!!; runCurrent()
        val second = h.manager.create("host-a")!!; runCurrent()
        h.manager.end(second); runCurrent()
        h.shells[1].second.closed.await()
        runCurrent()
        assertEquals(1, h.shells[1].second.closes)
        assertEquals(first, h.manager.state.value.selectedForHost("host-a")!!.sessionId)
        assertTrue(h.manager.state.value.sessions.single().connected)
        h.manager.send(first, "still alive\r"); runCurrent()
        assertEquals(listOf("still alive\r"), h.shells[0].second.input)
        assertFalse(h.shells[0].second.closed.isCompleted)
    }

    @Test fun `a new application instance starts empty and never restores or replays prior sessions`() = runTest {
        val h = Harness(this)
        val id = h.manager.create("host-a")!!; runCurrent()
        h.manager.updateDraft(id, "secret-draft")
        h.shells.single().second.output.send("secret-output"); runCurrent()
        val restarted = h.manager(this)
        assertTrue(restarted.state.value.sessions.isEmpty())
        assertTrue(restarted.state.value.selected.isEmpty())
        assertEquals(1, h.shells.size)
        restarted.send(id, "must not replay\r"); runCurrent()
        assertTrue(h.shells.single().second.input.isEmpty())
        restarted.enter("host-a"); runCurrent()
        val state = restarted.state.value.sessions.single()
        assertNotEquals(id, state.sessionId)
        assertTrue(state.connected)
        assertEquals("", state.draft)
        assertEquals("", state.output)
        assertEquals(2, h.shells.size)
        assertTrue(h.shells.last().second.input.isEmpty())
    }

    @Test fun `remote EOF and write failure affect only their own shell and never replay input`() = runTest {
        val h = Harness(this)
        val first = h.manager.create("host-a")!!; runCurrent()
        val second = h.manager.create("host-a")!!; runCurrent()
        h.shells[0].second.output.close(); runCurrent()
        assertFalse(h.manager.state.value.sessions.first().connected)
        assertTrue(h.manager.state.value.sessions.last().connected)
        h.shells[1].second.failWrite = true
        h.manager.send(second, "never replay\r"); runCurrent()
        assertTrue(h.manager.state.value.sessions.last().problem)
        h.manager.enter("host-a"); runCurrent()
        assertEquals(2, h.shells.size)
        h.manager.reconnect(first); runCurrent()
        assertTrue(h.shells.last().second.input.isEmpty())
    }

    @Test fun `ending during connection closes a late result without resurrecting the entry`() = runTest {
        val ready = CompletableDeferred<Unit>()
        val shell = Shell()
        val manager = SshTerminalSessions(backgroundScope, SshTerminalConnector {
            withContext(NonCancellable) { ready.await() }
            SshTerminalConnection(shell, AutoCloseable {})
        }, { true }, { 1234 })
        val id = manager.create("host-a")!!; runCurrent()
        manager.end(id)
        ready.complete(Unit); runCurrent()
        shell.closed.await()
        assertTrue(manager.state.value.sessions.isEmpty())
    }

}
