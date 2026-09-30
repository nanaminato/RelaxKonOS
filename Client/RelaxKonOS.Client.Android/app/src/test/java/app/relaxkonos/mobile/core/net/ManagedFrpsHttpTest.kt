package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class ManagedFrpsHttpTest {
    private suspend fun serve(payload: String, call: suspend (String, List<List<String>>) -> Unit) {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = payload.toByteArray(); exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }; server.start()
        try { call("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `frps save uses PUT main route with expected revision and no invented operation key`() = runTest {
        serve(FRPS_JSON) { url, requests ->
            val request = ManagedFrpsRequest(true, "127.0.0.1", 7000, listOf(TunnelPortRange(6000, 6010)), null, null, true, "new-token".toCharArray(), false, "127.0.0.1", null, null, null, 2)
            assertTrue(RelaxKonApi("test", "test").saveManagedFrps(url, "token", request) is ApiResult.Success)
            assertEquals("PUT", requests.single()[0]); assertEquals("/api/v1.0/tunnels/frps", requests.single()[1]); assertEquals("", requests.single()[3])
            assertTrue(requests.single()[2].contains("\"expectedRevision\":2")); assertTrue(requests.single()[2].contains("new-token"))
        }
    }
    @Test fun `secret editor is explicit GET and stop uses actual disconnected synchronous result`() = runTest {
        serve(FRPS_JSON.replace("\"token\":null", "\"token\":\"existing-secret\"")) { url, requests ->
            val result = RelaxKonApi("test", "test").managedFrpsEditing(url, "token") as ApiResult.Success<ManagedFrpsEditing>
            assertEquals("existing-secret", result.value.token!!.concatToString()); assertEquals("/api/v1.0/tunnels/frps/editor", requests.single()[1]); assertEquals("GET", requests.single()[0])
        }
        serve("""{"succeeded":true,"state":"disconnected","problemCode":""}""") { url, requests ->
            val result = RelaxKonApi("test", "test").stopManagedFrps(url, "token") as ApiResult.Success<TunnelResult>
            assertEquals(TunnelConnectionState.Disconnected, result.value.state); assertEquals("POST", requests.single()[0]); assertEquals("/api/v1.0/tunnels/frps/stop", requests.single()[1])
        }
    }
}
