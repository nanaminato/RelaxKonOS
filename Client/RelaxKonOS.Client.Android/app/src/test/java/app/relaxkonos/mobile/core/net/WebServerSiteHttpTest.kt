package app.relaxkonos.mobile.core.net

import app.relaxkonos.mobile.ui.manage.websites.WebSiteDraft
import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WebServerSiteHttpTest {
    private suspend fun serve(status: Int, payload: String, call: suspend (String, List<Triple<String, String, String>>) -> Unit) {
        val requests = mutableListOf<Triple<String, String, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            assertNull(exchange.requestHeaders.getFirst("Idempotency-Key"))
            requests += Triple(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString())
            if (status == 204) exchange.sendResponseHeaders(status, -1) else {
                val bytes = payload.toByteArray(); exchange.sendResponseHeaders(status, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }
            }
            exchange.close()
        }
        server.start()
        try { call("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `save uses POST current full definition and precise version without a fabricated operation`() = runTest {
        val request = WebSiteDraft.from(WebPublishingWire.site(WEB_SITE)).request()!!
        serve(200, WEB_SITE) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").saveWebServerSite(url, "token", "nginx", request) is ApiResult.Success)
            assertEquals("POST", requests.single().first)
            assertEquals("/api/v1.0/webservers/nginx/sites", requests.single().second)
            assertEquals(request.expectedUpdatedAt, JSONObject(requests.single().third).getString("expectedUpdatedAt"))
        }
    }
    @Test fun `DELETE carries observed version in its JSON body and accepts no-content response`() = runTest {
        serve(204, "") { url, requests ->
            assertTrue(RelaxKonApi("test", "test").deleteWebServerSite(url, "token", "a/b", "site1", "2026-09-30T00:00:00.1234567Z") is ApiResult.Success)
            assertEquals("DELETE", requests.single().first)
            assertEquals("/api/v1.0/webservers/a%2Fb/sites/site1", requests.single().second)
            assertEquals("2026-09-30T00:00:00.1234567Z", JSONObject(requests.single().third).getString("expectedUpdatedAt"))
        }
    }
    @Test fun `a returned operation payload is not mistaken for synchronous deletion`() = runTest {
        serve(202, WEB_OPERATION) { url, _ ->
            assertTrue(RelaxKonApi("test", "test").deleteWebServerSite(url, "token", "nginx", "site1", "2026-09-30T00:00:00.1234567Z") is ApiResult.Transport)
        }
    }
    @Test fun `stale writes stay conflicts and malformed save responses stay unknown`() = runTest {
        val request = WebSiteDraft.from(WebPublishingWire.site(WEB_SITE)).request()!!
        serve(409, """{"problemCode":"webserver.site_changed"}""") { url, _ ->
            assertEquals("webserver.site_changed", (RelaxKonApi("test", "test").saveWebServerSite(url, "token", "nginx", request) as ApiResult.Problem).code)
        }
        serve(200, "{}") { url, _ -> assertTrue(RelaxKonApi("test", "test").saveWebServerSite(url, "token", "nginx", request) is ApiResult.Transport) }
    }
}
