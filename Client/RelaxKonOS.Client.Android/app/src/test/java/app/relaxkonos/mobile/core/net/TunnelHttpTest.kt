package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class TunnelHttpTest {
    private suspend fun serve(status: Int, payload: String, call: suspend (String, List<List<String>>) -> Unit) {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = payload.toByteArray(); exchange.sendResponseHeaders(status, if (status == 204) -1 else bytes.size.toLong())
            if (status != 204) exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }; server.start()
        try { call("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `profile update sends PUT current revision and no invented idempotency key`() = runTest {
        serve(200, TUNNEL_PROFILE_JSON) { url, requests ->
            val request = TunnelProfileRequest("edge", "frps.test", 7000, TunnelAuth.Token, TunnelTls.Force, TunnelRuntimeMode.Managed, null, 2)
            assertTrue(RelaxKonApi("test", "test").saveTunnelProfile(url, "token", TUNNEL_PROFILE_ID, request) is ApiResult.Success)
            assertEquals("PUT", requests.single()[0]); assertEquals("/api/v1.0/tunnels/profiles/$TUNNEL_PROFILE_ID", requests.single()[1])
            assertTrue(requests.single()[2].contains("\"expectedRevision\":2")); assertEquals("", requests.single()[3])
        }
    }
    @Test fun `token goes only to dedicated write only endpoint and 204 has no reflection`() = runTest {
        serve(204, "") { url, requests ->
            assertTrue(RelaxKonApi("test", "test").setTunnelToken(url, "token", TUNNEL_PROFILE_ID, "private-token") is ApiResult.Success)
            assertEquals("/api/v1.0/tunnels/profiles/$TUNNEL_PROFILE_ID/secret", requests.single()[1])
            assertEquals("{\"token\":\"private-token\"}", requests.single()[2])
        }
    }
    @Test fun `apply reports actual starting while external detection uses explicit host path`() = runTest {
        serve(200, """{"succeeded":true,"state":"starting","problemCode":""}""") { url, requests ->
            val result = RelaxKonApi("test", "test").applyTunnelProfile(url, "token", TUNNEL_PROFILE_ID) as ApiResult.Success<TunnelResult>
            assertEquals(TunnelConnectionState.Starting, result.value.state); assertEquals("POST", requests.single()[0]); assertEquals("", requests.single()[3])
        }
        serve(200, TUNNEL_RUNTIME_JSON.replace("managed", "external")) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").detectTunnelRuntime(url, "token", "/opt/frp/frpc") is ApiResult.Success)
            assertEquals("/api/v1.0/tunnels/runtime/external/detect", requests.single()[1]); assertTrue(requests.single()[2].contains("executablePath"))
        }
    }
}
