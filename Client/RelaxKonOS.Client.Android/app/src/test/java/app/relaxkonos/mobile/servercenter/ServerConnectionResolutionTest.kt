package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** loopback 隧道与连接解析的契约测试。与 C# `ServerTunnelRules` 必须给出同一结论。 */
class ServerConnectionResolutionTest {

    private val installationId = "rki-" + "0123456789abcdef0123456789abcdef"

    @Test
    fun `only loopback addresses may host a tunnel`() {
        assertEquals("127.0.0.1", ServerTunnelRules.LOOPBACK_HOST)
        assertTrue(ServerTunnelRules.isLoopbackHost("127.0.0.1"))
        assertTrue(ServerTunnelRules.isLoopbackHost("127.5.5.5"))
        assertTrue(ServerTunnelRules.isLoopbackHost("::1"))
        assertTrue(ServerTunnelRules.isLoopbackHost("[::1]"))
        assertTrue(ServerTunnelRules.isLoopbackHost("::ffff:127.0.0.1"))
        assertFalse(ServerTunnelRules.isLoopbackHost("0.0.0.0"))
        assertFalse(ServerTunnelRules.isLoopbackHost("192.168.1.10"))
        assertFalse(ServerTunnelRules.isLoopbackHost("localhost"))
        assertFalse(ServerTunnelRules.isLoopbackHost(null))
        assertFalse(ServerTunnelRules.isAcceptableTunnelBindAddress("0.0.0.0"))
        assertTrue(ServerTunnelRules.isAcceptableTunnelBindAddress("127.0.0.1"))
        assertEquals(0, ServerTunnelRules.EPHEMERAL_PORT)
    }

    @Test
    fun `loopback base url keeps an optional base path`() {
        assertEquals("http://127.0.0.1:51000", ServerTunnelRules.buildLoopbackBaseUrl(51000))
        assertEquals(
            "http://127.0.0.1:51000/relaxkonos",
            ServerTunnelRules.buildLoopbackBaseUrl(51000, "relaxkonos/"),
        )
    }

    @Test
    fun `port is only read back from an explicit loopback address`() {
        assertEquals(51000, ServerTunnelRules.tryGetLoopbackPort("http://127.0.0.1:51000"))
        assertNull(ServerTunnelRules.tryGetLoopbackPort("http://example.com:51000"))
        assertNull(ServerTunnelRules.tryGetLoopbackPort("http://127.0.0.1"))
        assertNull(ServerTunnelRules.tryGetLoopbackPort("not a url"))
        assertNull(ServerTunnelRules.tryGetLoopbackPort(null))
    }

    @Test
    fun `managed tunnel identity is the installation id, not the port`() {
        val resolution = ServerTunnelRules.managedTunnel(installationId, 51000, null, 0L)
        assertEquals(ServerConnectionTransportKind.SshTunnel, resolution.transport)
        assertEquals(51000, resolution.localPort)
        assertEquals(ServerServiceIdKind.ManagedInstallation, resolution.identity.kind)
        assertEquals(installationId, resolution.identity.serviceId)
        assertEquals("http://127.0.0.1:51000", resolution.identity.effectiveBaseUrl)
    }

    @Test
    fun `rebinding a tunnel changes the address but never the login identity`() {
        val before = ServerTunnelRules.managedTunnel(installationId, 51000, null, 0L)
        val after = ServerTunnelRules.rebindTunnel(before, 52345, 1L)
        assertEquals(52345, after.localPort)
        assertEquals("http://127.0.0.1:52345", after.identity.effectiveBaseUrl)
        assertEquals(before.identity.serviceId, after.identity.serviceId)
        assertTrue(ServerConnectionIdentityRules.preservesIdentity(before.identity, after.identity))
    }

    @Test
    fun `rebinding keeps a base path`() {
        val before = ServerTunnelRules.managedTunnel(installationId, 51000, "relaxkonos", 0L)
        val after = ServerTunnelRules.rebindTunnel(before, 52345, 0L)
        assertEquals("http://127.0.0.1:52345/relaxkonos", after.identity.effectiveBaseUrl)
    }

    @Test
    fun `direct resolution uses the persistent url for both identity and address`() {
        val direct = ServerTunnelRules.direct("https://example.com/", 0L)
        assertEquals(ServerConnectionTransportKind.Direct, direct.transport)
        assertNull(direct.localPort)
        assertEquals(direct.identity.serviceId, direct.identity.effectiveBaseUrl)
    }

    @Test(expected = IllegalArgumentException::class)
    fun `a direct resolution cannot be rebound as a tunnel`() {
        ServerTunnelRules.rebindTunnel(ServerTunnelRules.direct("https://example.com/", 0L), 51000, 0L)
    }
}