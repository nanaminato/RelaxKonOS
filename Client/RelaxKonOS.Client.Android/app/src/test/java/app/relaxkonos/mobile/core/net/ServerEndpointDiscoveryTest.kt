package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerEndpointDiscoveryTest {
    @Test fun `present but empty unsupported components are rejected instead of silently removed`() {
        listOf("https://@server.example", "https://server.example?", "https://server.example#",
            "server.example/?", "server.example/#").forEach { assertTrue(it, ServerEndpointDiscovery.candidates(it).isEmpty()) }
        assertEquals(listOf("https://server.example"), ServerEndpointDiscovery.candidates("https://server.example/"))
    }
    @Test fun `canonical discovery matches saved identity for scheme host and default ports`() {
        assertEquals(listOf("https://server.example"), ServerEndpointDiscovery.candidates("HTTPS://SERVER.EXAMPLE:443/"))
        assertEquals(listOf("http://server.example"), ServerEndpointDiscovery.candidates("HtTp://SERVER.EXAMPLE:80/"))
        assertEquals(listOf("https://[::1]:5090"), ServerEndpointDiscovery.candidates("HTTPS://[::1]:5090/"))
    }

    @Test fun `invalid ports are rejected before network probing`() {
        listOf("https://server.example:0", "server.example:65536", "http://server.example:-1",
            "https://server.example:abc").forEach { assertTrue(ServerEndpointDiscovery.candidates(it).isEmpty()) }
        assertEquals(listOf("https://server.example:65535"), ServerEndpointDiscovery.candidates("https://server.example:65535"))
    }
    @Test
    fun `bare address tries https before http`() {
        assertEquals(
            listOf("https://server.example:5090", "http://server.example:5090"),
            ServerEndpointDiscovery.candidates("  server.example:5090/  "),
        )
    }

    @Test
    fun `explicit scheme remains an explicit user choice`() {
        assertEquals(
            listOf("http://10.0.2.2:5090"),
            ServerEndpointDiscovery.candidates("http://10.0.2.2:5090/"),
        )
    }

    @Test
    fun `credentials and paths are rejected`() {
        assertTrue(ServerEndpointDiscovery.candidates("https://user:password@server").isEmpty())
        assertTrue(ServerEndpointDiscovery.candidates("server.example/api").isEmpty())
    }
}
