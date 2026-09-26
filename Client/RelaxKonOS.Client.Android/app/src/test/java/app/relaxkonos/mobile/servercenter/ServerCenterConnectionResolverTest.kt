package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 连接解析器与会话的契约测试。
 *
 * 用假传输替换真实 SSH：解析器、会话与隧道身份规则才是被测对象，网络握手不在其中。
 * 真机上的 JSch 行为由 G2 的端到端验收覆盖。
 */
class ServerCenterConnectionResolverTest {

    private val now = 1_790_000_000_000L
    private val installationId = "rki-" + "a".repeat(32)
    private val keyBlob = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)

    private val endpoint = ServerCenterSshEndpoint.create("host.example", 22, "deploy")

    private fun observation() =
        ServerCenterHostKeyObservation(endpoint.host, endpoint.port, "ssh-ed25519", keyBlob)

    private fun credential() = SshCredential(SshCredentialKind.Password, "secret".toCharArray(), null)

    private fun target(installed: Boolean = false): ServerHostTarget {
        val created = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
        if (!installed) return created
        return created.copy(installationId = installationId)
    }

    private class Harness {
        val keyStorage = InMemoryHostKeyStorage()
        val targetStorage = InMemoryHostTargetStorage()
        val trust = ServerHostKeyTrustStore(keyStorage)
        val targets = ServerHostTargetStore(targetStorage)
        val factory = FakeTransportFactory()
        val resolver = DefaultServerCenterConnectionResolver(trust, targets, factory)
    }

    @Test
    fun `direct resolution uses the url as both identity and address`() {
        val harness = Harness()
        val resolution = harness.resolver.resolveDirect("https://example.com/", now)

        assertEquals(ServerConnectionTransportKind.Direct, resolution.transport)
        assertNull(resolution.localPort)
        assertEquals(ServerServiceIdKind.DirectUrl, resolution.identity.kind)
        assertEquals(resolution.identity.serviceId, resolution.identity.effectiveBaseUrl)
    }

    @Test
    fun `a first contact guard refuses the unknown host key`() {
        val harness = Harness()
        val guard = harness.resolver.prepareHostKeyGuard(target())

        assertEquals(ServerHostKeyTrust.Unknown, guard(observation()))
    }

    @Test
    fun `after the fingerprint is pinned the guard accepts the key`() {
        val harness = Harness()
        harness.trust.trust(endpoint, observation(), now)

        val guard = harness.resolver.prepareHostKeyGuard(target())
        assertEquals(ServerHostKeyTrust.Trusted, guard(observation()))
    }

    @Test
    fun `a changed host key is reported to the caller instead of being accepted`() {
        val harness = Harness()
        harness.trust.trust(endpoint, observation(), now)
        val rotated = ServerCenterHostKeyObservation(endpoint.host, endpoint.port, "ssh-ed25519", byteArrayOf(9, 9, 9))

        val guard = harness.resolver.prepareHostKeyGuard(target())
        assertEquals(ServerHostKeyTrust.Changed, guard(rotated))
    }

    @Test
    fun `connecting with an unpinned key throws and disposes the transport`() {
        val harness = Harness()
        val guard = harness.resolver.prepareHostKeyGuard(target())

        val error = runCatching { runBlocking { harness.resolver.connect(target(), credential(), guard) } }.exceptionOrNull()

        assertTrue(error is ServerCenterHostKeyRejectedException)
        assertEquals(ServerHostKeyTrust.Unknown, (error as ServerCenterHostKeyRejectedException).trust)
        assertEquals(1, harness.factory.created.size)
        assertTrue("a rejected handshake must not leave the transport open", harness.factory.created[0].closed)
    }

    @Test
    fun `connecting with a pinned key returns a live session`() {
        val harness = Harness()
        harness.trust.trust(endpoint, observation(), now)

        val session = runBlocking { harness.resolver.connect(target(), credential(), harness.resolver.prepareHostKeyGuard(target())) }

        assertTrue(session.sshTransport.isConnected)
        assertNotNull(session.observedHostKey)
        assertFalse(session.hasTunnel)
        assertNull(session.resolution)
        session.close()
        assertTrue(harness.factory.created[0].closed)
    }

    @Test
    fun `connecting by host id refreshes recency and rejects an unknown host`() {
        val harness = Harness()
        val stored = harness.targets.upsert(target(installed = true))
        harness.trust.trust(endpoint, observation(), now)

        val session = runBlocking { harness.resolver.connect(stored.hostId, credential(), now + 60_000) }

        assertEquals(now + 60_000, harness.targets.find(stored.hostId)?.lastUsedAtEpochMillis)
        session.close()

        val unknown = runCatching { runBlocking { harness.resolver.connect("rkhost-0000000000000000", credential(), now) } }
        assertTrue(unknown.exceptionOrNull() is IllegalStateException)
    }

    @Test
    fun `a managed tunnel keeps the installation id when the port changes`() {
        val harness = Harness()
        harness.trust.trust(endpoint, observation(), now)
        harness.factory.portSequence = listOf(51000, 52345)
        val session = runBlocking {
            harness.resolver.connect(target(installed = true), credential(), harness.resolver.prepareHostKeyGuard(target()))
        }

        val first = session.openOrRebindTunnel(5090, null, now)
        assertEquals(ServerConnectionTransportKind.SshTunnel, first.transport)
        assertEquals(51000, first.localPort)
        assertEquals(ServerServiceIdKind.ManagedInstallation, first.identity.kind)
        assertEquals(installationId, first.identity.serviceId)
        assertEquals("http://127.0.0.1:51000", first.identity.effectiveBaseUrl)

        val second = session.openOrRebindTunnel(5090, null, now + 1)
        assertEquals(52345, second.localPort)
        assertEquals("http://127.0.0.1:52345", second.identity.effectiveBaseUrl)
        assertEquals(installationId, second.identity.serviceId)
        assertTrue(ServerConnectionIdentityRules.preservesIdentity(first.identity, second.identity))

        // 换端口先开新再关旧：旧监听必须被关掉，否则会一直占着端口。
        assertTrue(harness.factory.created[0].tunnels[0].closed)
        assertFalse(harness.factory.created[0].tunnels[1].closed)

        session.close()
        assertTrue(harness.factory.created[0].tunnels[1].closed)
        assertTrue(harness.factory.created[0].closed)
    }

    @Test
    fun `a tunnel without a verified installation is refused`() {
        val harness = Harness()
        harness.trust.trust(endpoint, observation(), now)
        val session = runBlocking {
            harness.resolver.connect(target(installed = false), credential(), harness.resolver.prepareHostKeyGuard(target()))
        }

        val error = runCatching { session.openOrRebindTunnel(5090, null, now) }.exceptionOrNull()
        assertTrue(error is IllegalStateException)
        session.close()
    }
}

/** 假传输：记录连接与隧道生命周期，不触碰网络。 */
private class FakeTransport(private val portSequence: List<Int>) : ServerCenterSshTransport {

    val tunnels = mutableListOf<FakeTunnel>()
    var closed = false
        private set

    private var portIndex = 0

    override var isConnected = false
        private set

    override var observedHostKey: ServerCenterHostKeyObservation? = null
        private set

    override suspend fun connect(
        endpoint: ServerCenterSshEndpoint,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ) {
        val seen = ServerCenterHostKeyObservation(endpoint.host, endpoint.port, "ssh-ed25519", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))
        observedHostKey = seen
        val trust = hostKeyGuard(seen)
        if (trust != ServerHostKeyTrust.Trusted) {
            throw ServerCenterHostKeyRejectedException(seen, trust)
        }
        isConnected = true
    }

    override suspend fun run(command: String): ServerCenterSshCommandResult =
        ServerCenterSshCommandResult(0, "", "")

    override suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult =
        ServerCenterSshCommandResult(0, "", "")

    override suspend fun upload(
        content: InputStream,
        contentLength: Long?,
        remotePath: String,
        progress: ((Double) -> Unit)?,
    ) = Unit

    override suspend fun download(remotePath: String, destination: OutputStream) = Unit

    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel {
        val port = portSequence[portIndex.coerceAtMost(portSequence.lastIndex)]
        portIndex++
        return FakeTunnel(port, basePath).also { tunnels += it }
    }

    override fun close() {
        closed = true
        isConnected = false
    }
}

private class FakeTunnel(override val localPort: Int, basePath: String?) : ServerCenterSshTunnel {
    override val localBaseUrl: String = ServerTunnelRules.buildLoopbackBaseUrl(localPort, basePath)
    var closed = false
        private set

    override fun close() {
        closed = true
    }
}

private class FakeTransportFactory : ServerCenterSshTransportFactory {
    val created = mutableListOf<FakeTransport>()
    var portSequence: List<Int> = listOf(51000)

    override fun create(): ServerCenterSshTransport = FakeTransport(portSequence).also { created += it }
}

/** 最小的协程运行器：测试里没有协程依赖，只用它把 suspend 调用跑完。 */
private fun <T> runBlocking(block: suspend () -> T): T =
    kotlinx.coroutines.runBlocking { block() }
