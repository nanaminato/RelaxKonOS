package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class ProxyHttpTest {
    @Test fun `TUN uses profile body and stable key while settings PUT retains nested options`() = runTest {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            if (exchange.requestURI.path.endsWith("/settings")) { exchange.sendResponseHeaders(204, -1); exchange.close() }
            else { val bytes = """{"operationId":"$PROXY_ID"}""".toByteArray(); exchange.sendResponseHeaders(202, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close() }
        }; server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            assertTrue(api.proxyQueue(url, "token", ProxyAction.EnableTun, PROXY_ID, "tun-key") is ApiResult.Success)
            assertTrue(api.saveProxySettings(url, "token", ProxyDiagnosticsWire.settings(PROXY_SETTINGS)) is ApiResult.Success)
            assertEquals("/api/v1.0/proxy/tun/enable", requests[0][1]); assertEquals("tun-key", requests[0][3]); assertTrue(requests[0][2].contains(PROXY_ID))
            assertEquals("PUT", requests[1][0]); assertEquals("/api/v1.0/proxy/settings", requests[1][1]); assertEquals("", requests[1][3])
            assertTrue(requests[1][2].contains("\"guardIntervalSeconds\":42"))
        } finally { server.stop(0) }
    }
    @Test fun `queued actions keep stable key and current route while YAML apply is synchronous`() = runTest {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty(), exchange.requestHeaders.getFirst("Authorization"))
            if (exchange.requestURI.path.endsWith("/apply")) { exchange.sendResponseHeaders(204, -1); exchange.close() }
            else { val bytes = """{"operationId":"$PROXY_ID"}""".toByteArray(); exchange.sendResponseHeaders(202, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close() }
        }; server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            assertTrue(api.proxyQueue(url, "token", ProxyAction.Start, null, "original-key") is ApiResult.Success)
            assertTrue(api.proxyQueue(url, "token", ProxyAction.RefreshSubscription, PROXY_ID, "refresh-key") is ApiResult.Success)
            assertTrue(api.applyProxyConfiguration(url, "token", PROXY_ID, "mode: rule\n") is ApiResult.Success)
            assertEquals("/api/v1.0/proxy/lifecycle/start", requests[0][1]); assertEquals("original-key", requests[0][3])
            assertEquals("/api/v1.0/proxy/subscriptions/$PROXY_ID/refresh", requests[1][1]); assertEquals("refresh-key", requests[1][3])
            assertEquals("/api/v1.0/proxy/profiles/$PROXY_ID/configuration/apply", requests[2][1]); assertEquals("", requests[2][3])
            assertTrue(requests[2][2].contains("\"yaml\":\"mode: rule\\n\"")); assertTrue(requests.all { it[0] == "POST" && it[4] == "Bearer token" })
        } finally { server.stop(0) }
    }
}
