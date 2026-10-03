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
        init { if (trusted) trust.trust(ServerCenterSshEndpoint.create("example.test", 22, "alice"), observation, 0) }
        val resolver = DefaultServerCenterConnectionResolver(trust, targets, object : ServerCenterSshTransportFactory {
            override fun create() = ForwardTransport(observation, failPort).also { transports += it }
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

private class ForwardTransport(private val key: ServerCenterHostKeyObservation, private val failPort: Boolean) : ServerCenterSshTransport {
    var connected = false; var opened = 0; var testedPort: Int? = null
    override val isConnected get() = connected
    override val observedHostKey get() = key
    override suspend fun connect(endpoint: ServerCenterSshEndpoint, credential: SshCredential, hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust) {
        val result = hostKeyGuard(key); if (result != ServerHostKeyTrust.Trusted) throw ServerCenterHostKeyRejectedException(key, result)
        connected = true
    }
    override fun openLocalForward(remotePort: Int, preferredLocalPort: Int?): ServerCenterSshTunnel {
        if (failPort) throw IllegalStateException("Port conflict")
        opened++
        return object : ServerCenterSshTunnel {
            override val localPort = preferredLocalPort ?: 12000
            override val localBaseUrl = "http://127.0.0.1:$localPort"
            override fun close() { }
        }
    }
    override suspend fun testLoopbackPort(remotePort: Int): Boolean { testedPort = remotePort; return connected }
    override suspend fun fileInfo(remotePath: String): SshFileEntry? = error("not used")
    override suspend fun uploadNew(content: InputStream, contentLength: Long?, remotePath: String, progress: ((Long) -> Unit)?) = error("not used")
    override suspend fun copyFile(sourcePath: String, destinationPath: String, maximumBytes: Long) = error("not used")
    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel = error("Managed tunnel must not be used")
    override fun close() { connected = false }
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
