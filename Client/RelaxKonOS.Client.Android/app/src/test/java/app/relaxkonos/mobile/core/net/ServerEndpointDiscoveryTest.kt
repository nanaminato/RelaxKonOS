package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerEndpointDiscoveryTest {
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
