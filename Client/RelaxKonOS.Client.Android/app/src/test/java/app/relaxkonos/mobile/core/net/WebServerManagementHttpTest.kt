package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WebServerManagementHttpTest {
    private data class Request(val method: String, val route: String, val token: String?, val key: String?, val body: String)
    private suspend fun serve(code: Int, payload: String, block: suspend (String, List<Request>) -> Unit) {
        val requests = mutableListOf<Request>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Request(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestHeaders.getFirst("Authorization"),
                exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = payload.toByteArray()
            exchange.sendResponseHeaders(code, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try { block("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `mutations use canonical routes stable keys and explicit integration confirmation`() = runTest {
        serve(202, WEB_OPERATION) { url, requests ->
            val api = RelaxKonApi("test", "test")
            assertTrue(api.integrateWebServer(url, "token", "a/b", true, "original") is ApiResult.Success)
            assertTrue(api.webServerLifecycle(url, "token", "nginx", WebServerAction.Reload, "original") is ApiResult.Success)
            assertEquals(listOf("/api/v1.0/webservers/integration-candidates/a%2Fb/integrate", "/api/v1.0/webservers/nginx/lifecycle/reload"), requests.map { it.route })
            assertTrue(requests.all { it.method == "POST" && it.token == "Bearer token" && it.key == "original" })
            assertTrue(JSONObject(requests.first().body).getBoolean("confirmed"))
        }
    }
    @Test fun `lookup and cancel preserve the original operation ID`() = runTest {
        serve(200, WEB_OPERATION) { url, requests ->
            val api = RelaxKonApi("test", "test")
            val id = WebPublishingWire.operation(WEB_OPERATION).operationId
            assertTrue(api.webServerOperation(url, "token", id) is ApiResult.Success)
            assertTrue(api.cancelWebServerOperation(url, "token", id, "cancel-key") is ApiResult.Success)
            assertEquals("GET", requests[0].method)
            assertEquals("/api/v1.0/webservers/operations/$id/cancel", requests[1].route)
            assertEquals("cancel-key", requests[1].key)
        }
    }
    @Test fun `malformed success remains unknown while refusal preserves problem`() = runTest {
        serve(202, "{}") { url, _ -> assertTrue(RelaxKonApi("test", "test").webServerLifecycle(url, "token", "nginx", WebServerAction.Start, "key") is ApiResult.Transport) }
        serve(409, """{"problemCode":"webserver.managed_required"}""") { url, _ ->
            val result = RelaxKonApi("test", "test").webServerLifecycle(url, "token", "nginx", WebServerAction.Start, "key")
            assertEquals("webserver.managed_required", (result as ApiResult.Problem).code)
        }
    }
}
