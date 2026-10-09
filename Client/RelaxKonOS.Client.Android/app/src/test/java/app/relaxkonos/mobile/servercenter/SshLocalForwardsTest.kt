package app.relaxkonos.mobile.servercenter

import java.io.InputStream
import java.io.OutputStream
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class SshLocalForwardsTest {
    @Test fun `failed cleanup of a late cancelled session stays tracked after another listener started`() = runTest {
        val h = Harness(backgroundScope)
        val release = CompletableDeferred<Unit>()
        h.connectGate = { release.await() }
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080))
        runCurrent()
        h.transports.first().failTransportClose = true
        h.manager.stopAll()
        h.connectGate = null
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8081))
        h.manager.state.first { !it.busy }
        release.complete(Unit)
        runCurrent()
        assertTrue(h.manager.state.value.cleanupUncertain)
        assertEquals("cleanup", h.manager.state.value.problem)
        assertTrue(h.transports.first().isConnected)
        h.manager.stopAll()
        assertTrue(h.manager.state.value.cleanupUncertain)
        assertFalse(h.transports.last().isConnected)
        h.transports.first().failTransportClose = false
        h.manager.stopAll()
        assertFalse(h.manager.state.value.cleanupUncertain)
        assertTrue(h.transports.none { it.isConnected })
        h.manager.clearWorkspace()
    }
    @Test fun `batch stop attempts every resource and blocks starts until failed cleanup is retried`() = runTest {
        val h = Harness(backgroundScope)
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080, 12001)); h.manager.state.first { !it.busy }
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8081, 12002)); h.manager.state.first { !it.busy }
        val firstId = h.manager.state.value.items.first().id
        h.transports.first().failTunnelClose = true
        h.manager.stopAll()
        assertTrue(h.transports.none { it.isConnected })
        assertFalse(h.manager.state.value.busy)
        assertTrue(h.manager.state.value.cleanupUncertain)
        assertEquals("cleanup", h.manager.state.value.problem)
        assertEquals(listOf(SshForwardStatus.CleanupPending, SshForwardStatus.Stopped), h.manager.state.value.items.map { it.status })
        val rejected = mutableListOf<Boolean>()
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8082), onComplete = { rejected += it })
        assertEquals(listOf(false), rejected)
        assertEquals(2, h.transports.size)
        h.manager.remove(firstId)
        assertTrue(h.manager.state.value.items.any { it.id == firstId })
        h.transports.first().failTunnelClose = false
        h.manager.stopAll()
        assertFalse(h.manager.state.value.cleanupUncertain)
        assertNull(h.manager.state.value.problem)
        h.manager.clearWorkspace()
    }

    @Test fun `workspace clearing retains failed transport cleanup and can retry despite closed session wrapper`() = runTest {
        val h = Harness(backgroundScope)
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080)); h.manager.state.first { !it.busy }
        h.transports.single().failTransportClose = true
        h.manager.clearWorkspace()
        assertTrue(h.manager.state.value.cleanupUncertain)
        assertEquals(1, h.manager.state.value.items.size)
        assertTrue(h.transports.single().isConnected)
        h.transports.single().failTransportClose = false
        h.manager.clearWorkspace()
        assertFalse(h.manager.state.value.cleanupUncertain)
        assertFalse(h.transports.single().isConnected)
        assertTrue(h.manager.state.value.items.isEmpty())
    }
    @Test fun `late cancelled handshake cannot replace a newer forward or stop its keeper`() = runTest {
        val h = Harness(backgroundScope)
        val release = CompletableDeferred<Unit>()
        h.connectGate = { release.await() }
        val results = mutableListOf<Pair<String, Boolean>>()
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080, 12001), onComplete = { results += "old" to it })
        runCurrent()
        assertTrue(h.manager.state.value.busy)
        h.manager.stopAll()
        h.connectGate = null
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8081, 12002), onComplete = { results += "new" to it })
        h.manager.state.first { !it.busy }
        val stops = h.keeperStops
        release.complete(Unit)
        runCurrent()
        assertEquals(listOf(8081), h.manager.state.value.items.map { it.request.remotePort })
        assertEquals(SshForwardStatus.Running, h.manager.state.value.items.single().status)
        assertFalse(h.transports.first().isConnected)
        assertTrue(h.transports.last().isConnected)
        assertEquals(stops, h.keeperStops)
        assertEquals(listOf("new" to true, "old" to false), results)
        h.manager.clearWorkspace()
    }

    @Test fun `old keeper failure cannot stop a newer listener`() = runTest {
        val h = Harness(backgroundScope)
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080))
        h.manager.state.first { !it.busy }
        val oldLease = h.manager.lease
        h.manager.stopAll()
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8081))
        h.manager.state.first { !it.busy }
        h.manager.keeperFailed(oldLease)
        assertEquals(SshForwardStatus.Running, h.manager.state.value.items.last().status)
        assertNull(h.manager.state.value.problem)
        assertTrue(h.transports.last().isConnected)
        h.manager.clearWorkspace()
    }
    @Test fun `completion reports actual listener success and replacement failure after busy is cleared`() = runTest {
        val h = Harness(backgroundScope)
        val results = mutableListOf<Boolean>()
        val request = SshLocalForwardRequest(8080, 12001)
        h.manager.start(h.target.hostId, request, onComplete = { assertFalse(h.manager.state.value.busy); results += it })
        h.manager.state.first { !it.busy }
        assertEquals(listOf(true), results)
        val id = h.manager.state.value.items.single().id
        h.failPort = true
        h.manager.start(h.target.hostId, request.copy(remotePort = 8081), id) { assertFalse(h.manager.state.value.busy); results += it }
        h.manager.state.first { !it.busy }
        assertEquals(listOf(true, false), results)
        assertEquals(SshForwardStatus.Stopped, h.manager.state.value.items.single().status)
        h.manager.clearWorkspace()
    }

    @Test fun `rejected input and cancellation before launch each complete once without opening a listener`() = runTest {
        val h = Harness(backgroundScope)
        val results = mutableListOf<Boolean>()
        h.manager.start(h.target.hostId, SshLocalForwardRequest(0), onComplete = { results += it })
        assertEquals(listOf(false), results)
        assertTrue(h.transports.isEmpty())
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080), onComplete = { results += it })
        h.manager.stopAll()
        runCurrent()
        assertEquals(listOf(false, false), results)
        assertTrue(h.transports.isEmpty())
        assertFalse(h.manager.state.value.busy)
    }
    @Test fun `requests keep both endpoints on loopback and reject authorities and schemes`() {
        val base = SshLocalForwardRequest(8080)
        assertEquals("http://127.0.0.1:12345/", base.localUrl(12345))
        assertTrue(base.copy(scheme = "https", pathAndQuery = "/ui?a=b").valid())
        listOf(base.copy(remotePort = 0), base.copy(preferredLocalPort = 80), base.copy(scheme = "file"),
            base.copy(pathAndQuery = "//evil.test/"), base.copy(pathAndQuery = "https://evil.test/"),
            base.copy(pathAndQuery = "/#fragment"), base.copy(pathAndQuery = "/\\evil"), base.copy(pathAndQuery = "/\n")).forEach { assertFalse(it.valid()) }
    }
    private class Harness(scope: CoroutineScope, trusted: Boolean = true) {
        val targets = ServerHostTargetStore(InMemoryHostTargetStorage())
        val target = targets.upsert(ServerHostTargetRules.create("example.test", 22, "alice", null, 0))
        val observation = ServerCenterHostKeyObservation("example.test", 22, "ssh-ed25519", byteArrayOf(1, 2, 3))
        val trust = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        val transports = mutableListOf<ForwardTransport>()
        var workspace: String? = target.hostId
        var failPort = false; var keeperStarts = 0; var keeperStops = 0
        var connectGate: (suspend () -> Unit)? = null
        init { if (trusted) trust.trust(ServerCenterSshEndpoint.create("example.test", 22, "alice"), observation, 0) }
        val resolver = DefaultServerCenterConnectionResolver(trust, targets, object : ServerCenterSshTransportFactory {
            override fun create() = ForwardTransport(observation, failPort, connectGate).also { transports += it }
        })
        val manager = SshLocalForwardManager(resolver, targets, { "secret".toCharArray() }, { workspace }, scope,
            { keeperStarts++ }, { keeperStops++ })
    }
    @Test fun `dedicated sessions bind requested port and stopping one leaves another running`() = runTest {
        val h = Harness(backgroundScope)
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8080, 12001)); h.manager.state.first { !it.busy }
        h.manager.start(h.target.hostId, SshLocalForwardRequest(8081, 12002)); h.manager.state.first { !it.busy }
        assertEquals(2, h.transports.size); assertEquals(listOf(12001, 12002), h.manager.state.value.items.map { it.localPort })
        h.manager.stop(h.manager.state.value.items.first().id)
        assertFalse(h.transports[0].isConnected); assertTrue(h.transports[1].isConnected)
        h.manager.clearWorkspace(); assertTrue(h.transports.none { it.isConnected }); assertTrue(h.manager.state.value.items.isEmpty())
    }
    @Test fun `failed replacement leaves original row stopped and never falls back to another port`() = runTest {
        val h = Harness(backgroundScope); val request = SshLocalForwardRequest(8080, 12001)
        h.manager.start(h.target.hostId, request); h.manager.state.first { !it.busy }
        val id = h.manager.state.value.items.single().id; h.failPort = true
        h.manager.start(h.target.hostId, request.copy(remotePort = 8081), id); h.manager.state.first { !it.busy }
        assertEquals(SshForwardStatus.Stopped, h.manager.state.value.items.single().status)
        assertEquals("connect", h.manager.state.value.problem); assertTrue(h.transports.none { it.isConnected }); h.manager.clearWorkspace()
    }
    @Test fun `unknown host key prevents listener creation and releases session`() = runTest {
        val h = Harness(backgroundScope, false); h.manager.start(h.target.hostId, SshLocalForwardRequest(8080)); h.manager.state.first { !it.busy }
        assertEquals("trust", h.manager.state.value.problem); assertTrue(h.manager.state.value.items.isEmpty())
        assertFalse(h.transports.single().isConnected); assertEquals(0, h.transports.single().opened); h.manager.clearWorkspace()
    }
    @Test fun `remote TCP test records observation without changing the target`() = runTest {
        val h = Harness(backgroundScope); h.manager.start(h.target.hostId, SshLocalForwardRequest(8080)); h.manager.state.first { !it.busy }
        val row = h.manager.state.value.items.single(); h.manager.test(row.id); h.manager.state.first { !it.busy }
        assertEquals(8080, h.transports.single().testedPort); assertEquals(true, h.manager.state.value.items.single().reachable)
        assertNotNull(h.manager.state.value.items.single().testedAtMillis); h.manager.clearWorkspace()
    }
    @Test fun `disconnection stops listener and does not reconnect automatically`() = runTest {
        val h = Harness(backgroundScope); h.manager.start(h.target.hostId, SshLocalForwardRequest(8080)); h.manager.state.first { !it.busy }
        h.transports.single().connected = false; advanceTimeBy(3_001); runCurrent()
        assertEquals(SshForwardStatus.Disconnected, h.manager.state.value.items.single().status)
        assertEquals(1, h.transports.size); h.manager.clearWorkspace()
    }
}

private class ForwardTransport(private val key: ServerCenterHostKeyObservation, private val failPort: Boolean,
    private val connectGate: (suspend () -> Unit)? = null) : ServerCenterSshTransport {
    var connected = false; var opened = 0; var testedPort: Int? = null
    var failTunnelClose = false
    var failTransportClose = false
    override val isConnected get() = connected
    override val observedHostKey get() = key
    override suspend fun connect(endpoint: ServerCenterSshEndpoint, credential: SshCredential, hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust) {
        val result = hostKeyGuard(key); if (result != ServerHostKeyTrust.Trusted) throw ServerCenterHostKeyRejectedException(key, result)
        if (connectGate != null) withContext(NonCancellable) { connectGate.invoke(); connected = true }
        else connected = true
    }
    override fun openLocalForward(remotePort: Int, preferredLocalPort: Int?): ServerCenterSshTunnel {
        if (failPort) throw IllegalStateException("Port conflict")
        opened++
        return object : ServerCenterSshTunnel {
            override val localPort = preferredLocalPort ?: 12000
            override val localBaseUrl = "http://127.0.0.1:$localPort"
            override fun close() { if (failTunnelClose) throw IllegalStateException("test tunnel cleanup failed") }
        }
    }
    override suspend fun testLoopbackPort(remotePort: Int): Boolean { testedPort = remotePort; return connected }
    override suspend fun fileInfo(remotePath: String): SshFileEntry? = error("not used")
    override suspend fun uploadNew(content: InputStream, contentLength: Long?, remotePath: String, progress: ((Long) -> Unit)?) = error("not used")
    override suspend fun copyFile(sourcePath: String, destinationPath: String, maximumBytes: Long) = error("not used")
    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel = error("Managed tunnel must not be used")
    override fun close() { if (failTransportClose) throw IllegalStateException("test transport cleanup failed"); connected = false }
    override suspend fun run(command: String): ServerCenterSshCommandResult = error("No commands")
    override suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult = error("No commands")
    override suspend fun openTerminal(): ServerCenterSshTerminal = error("No terminal")
    override suspend fun upload(content: InputStream, contentLength: Long?, remotePath: String, progress: ((Double) -> Unit)?) = error("No SFTP")
    override suspend fun download(remotePath: String, destination: OutputStream) = error("No SFTP")
    override suspend fun listDirectory(remotePath: String): List<SshFileEntry> = error("No SFTP")
    override suspend fun createDirectory(remotePath: String) = error("No SFTP")
    override suspend fun delete(remotePath: String, recursive: Boolean) = error("No SFTP")
    override suspend fun rename(sourcePath: String, destinationPath: String) = error("No SFTP")
}
