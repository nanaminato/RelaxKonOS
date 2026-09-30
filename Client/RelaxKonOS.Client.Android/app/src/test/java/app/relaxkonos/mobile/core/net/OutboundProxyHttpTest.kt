package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class OutboundProxyHttpTest {
    private data class Request(val method: String, val route: String, val authorization: String?, val body: String)
    private suspend fun serve(status: Int, response: String, block: suspend (String, List<Request>) -> Unit) {
        val requests = mutableListOf<Request>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Request(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestHeaders.getFirst("Authorization"), exchange.requestBody.readBytes().decodeToString())
            val bytes = response.toByteArray()
            exchange.sendResponseHeaders(status, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try { block("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `GET PUT DELETE share authoritative route and authenticated readback`() = runTest {
        serve(200, PROXY_STATUS) { url, requests ->
            val api = RelaxKonApi("test", "test")
            val settings = (api.outboundProxyStatus(url, "token") as ApiResult.Success).value.settings
            assertTrue(api.saveOutboundProxy(url, "token", settings, true) is ApiResult.Success)
            assertTrue(api.clearOutboundProxy(url, "token") is ApiResult.Success)
            assertEquals(listOf("GET", "PUT", "DELETE"), requests.map { it.method })
            assertTrue(requests.all { it.route == "/api/v1.0/docker/proxy" && it.authorization == "Bearer token" })
            assertEquals("", requests.last().body)
            val request = JSONObject(requests[1].body)
            assertEquals(settings.httpProxy, request.getString("httpProxy"))
            assertTrue(request.getBoolean("confirmed"))
        }
    }
    @Test fun `named refusal stays a problem and malformed write leaves result unknown`() = runTest {
        serve(400, """{"problemCode":"docker.proxy.problem.configuration_invalid"}""") { url, _ ->
            val result = RelaxKonApi("test", "test").saveOutboundProxy(url, "token", OutboundProxyWire.status(PROXY_STATUS).settings, true)
            assertEquals("docker.proxy.problem.configuration_invalid", (result as ApiResult.Problem).code)
        }
        serve(200, "{}") { url, _ ->
            assertTrue(RelaxKonApi("test", "test").clearOutboundProxy(url, "token") is ApiResult.Transport)
        }
    }
}
